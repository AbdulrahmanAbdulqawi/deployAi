using System.Text.Json;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;

namespace DeployAI.Infrastructure.GitHub;

public interface IRepositoryClassifier
{
    Task<DeploymentPlan> ClassifyAsync(
        string accessToken,
        string owner,
        string repo,
        string? gitRef,
        RepositoryClassificationOptions? options,
        CancellationToken cancellationToken);
}

public sealed class RepositoryClassifier : IRepositoryClassifier
{
    private readonly IGitHubService _gitHubService;
    private readonly IWebsiteBuildProfileDiscovery _websiteDiscovery;
    private readonly IServerBuildProfileDiscovery _serverDiscovery;
    private readonly IDatabaseRequirementDetector _databaseRequirementDetector;

    public RepositoryClassifier(
        IGitHubService gitHubService,
        IWebsiteBuildProfileDiscovery websiteDiscovery,
        IServerBuildProfileDiscovery serverDiscovery,
        IDatabaseRequirementDetector databaseRequirementDetector)
    {
        _gitHubService = gitHubService;
        _websiteDiscovery = websiteDiscovery;
        _serverDiscovery = serverDiscovery;
        _databaseRequirementDetector = databaseRequirementDetector;
    }

    public async Task<DeploymentPlan> ClassifyAsync(
        string accessToken,
        string owner,
        string repo,
        string? gitRef,
        RepositoryClassificationOptions? options,
        CancellationToken cancellationToken)
    {
        var preferCoolify = options?.PreferCoolify == true;
        var websiteProfile = await _websiteDiscovery.DiscoverAsync(
            accessToken, owner, repo, string.Empty, gitRef, cancellationToken);
        var serverProfile = await _serverDiscovery.DiscoverAsync(
            accessToken, owner, repo, string.Empty, gitRef, cancellationToken);

        var websiteRoot = websiteProfile?.RootDirectory ?? string.Empty;
        var serverRoot = serverProfile.ServiceDirectory ?? serverProfile.RootDirectory ?? string.Empty;

        var hasWebsite = websiteProfile?.Framework is not null ||
                         await HasStaticIndexHtmlAsync(
                             accessToken, owner, repo, websiteRoot, gitRef, cancellationToken);
        var hasServer = serverProfile.Framework is not null;

        if (hasWebsite && hasServer &&
            string.Equals(websiteRoot, serverRoot, StringComparison.OrdinalIgnoreCase))
        {
            return BuildSameRootAmbiguousPlan(websiteProfile!, serverProfile);
        }

        if (!hasWebsite && !hasServer)
        {
            return BuildLowConfidencePlan();
        }

        if (hasWebsite && !hasServer && websiteProfile is not null && IsAmbiguousFrontendOnly(websiteProfile))
        {
            return BuildAmbiguousFrontendPlan(websiteProfile);
        }

        var databaseProfile = hasServer
            ? await DetectDatabaseRequirementsAsync(accessToken, owner, repo, serverRoot, gitRef, cancellationToken)
            : new DatabaseRequirementProfile(false, false, []);

        var parts = BuildParts(websiteProfile, serverProfile, databaseProfile, hasWebsite, hasServer, preferCoolify);
        var summary = BuildPlainSummary(websiteProfile, serverProfile, databaseProfile, hasWebsite, hasServer, preferCoolify);
        var planKind = ResolvePlanKind(parts, hasWebsite, hasServer, preferCoolify);

        return new DeploymentPlan(parts, "high", summary, PlanKind: planKind);
    }

    private static DeploymentPlanKind ResolvePlanKind(
        IReadOnlyList<DeploymentPlanPart> parts,
        bool hasWebsite,
        bool hasServer,
        bool preferCoolify)
    {
        if (!preferCoolify)
        {
            return DeploymentPlanKind.Default;
        }

        if (hasWebsite && hasServer &&
            parts.Any(part => part.Role == "website" && IsCoolifyProvider(part.ProviderName)) &&
            parts.Any(part => part.Role == "server" && IsCoolifyProvider(part.ProviderName)))
        {
            return DeploymentPlanKind.CoolifyFullStack;
        }

        if (parts.Any(part => IsCoolifyProvider(part.ProviderName)))
        {
            return DeploymentPlanKind.CoolifySingle;
        }

        return DeploymentPlanKind.Default;
    }

    private static bool IsCoolifyProvider(string providerName) =>
        string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);

    private static DeploymentPlan BuildSameRootAmbiguousPlan(
        FrontendBuildProfile websiteProfile,
        ServerBuildProfile serverProfile)
    {
        var websitePart = ToWebsitePart(websiteProfile, preferCoolify: false);
        var serverPart = ToServerPart(serverProfile, preferCoolify: false);

        var question = new ClarifyingQuestion(
            "Does your app need to run code on a server, or is it just pages?",
            [
                new ClarifyingQuestionOption(
                    "pages",
                    "Just pages",
                    "Host the web experience without treating the whole repo as a backend service.",
                    [websitePart]),
                new ClarifyingQuestionOption(
                    "server",
                    "It needs a server",
                    "Run the backend or service code and keep it online.",
                    [serverPart])
            ]);

        return new DeploymentPlan(
            [],
            "low",
            "This repo looks like it could be a website, a server, or both in one place. We want to get that right.",
            question);
    }

    private static IReadOnlyList<DeploymentPlanPart> BuildParts(
        FrontendBuildProfile? websiteProfile,
        ServerBuildProfile serverProfile,
        DatabaseRequirementProfile databaseProfile,
        bool hasWebsite,
        bool hasServer,
        bool preferCoolify)
    {
        var parts = new List<DeploymentPlanPart>();

        if (hasWebsite && websiteProfile is not null)
        {
            parts.Add(ToWebsitePart(websiteProfile, preferCoolify));
        }

        if (hasServer)
        {
            parts.Add(ToServerPart(serverProfile, preferCoolify));
        }

        if (databaseProfile.RequiresPostgres)
        {
            parts.Add(new DeploymentPlanPart(
                "database",
                preferCoolify ? ProviderNameValues.Coolify : ProviderNameValues.Railway,
                DatabaseEngine: "postgres"));
        }

        if (databaseProfile.RequiresRedis)
        {
            parts.Add(new DeploymentPlanPart(
                "database",
                preferCoolify ? ProviderNameValues.Coolify : ProviderNameValues.Railway,
                DatabaseEngine: "redis"));
        }

        return parts;
    }

    private static DeploymentPlanPart ToWebsitePart(FrontendBuildProfile profile, bool preferCoolify) =>
        new(
            "website",
            preferCoolify ? ProviderNameValues.Coolify : ProviderNameValues.Vercel,
            RootDirectory: profile.RootDirectory,
            BuildCommand: profile.BuildCommand,
            InstallCommand: profile.InstallCommand,
            OutputDirectory: profile.OutputDirectory,
            Framework: profile.Framework);

    private static DeploymentPlanPart ToServerPart(ServerBuildProfile profile, bool preferCoolify) =>
        new(
            "server",
            ShouldUseCoolifyForServer(profile, preferCoolify)
                ? ProviderNameValues.Coolify
                : ProviderNameValues.Railway,
            RootDirectory: profile.RootDirectory,
            ServiceDirectory: profile.ServiceDirectory ?? profile.RootDirectory,
            BuildCommand: profile.BuildCommand,
            InstallCommand: profile.InstallCommand,
            StartCommand: profile.StartCommand,
            Framework: profile.Framework,
            DockerfilePath: profile.DockerfilePath);

    private static bool ShouldUseCoolifyForServer(ServerBuildProfile profile, bool preferCoolify) =>
        preferCoolify ||
        !string.IsNullOrWhiteSpace(profile.DockerfilePath) ||
        string.Equals(profile.Framework, "docker", StringComparison.OrdinalIgnoreCase);

    private static DeploymentPlan BuildLowConfidencePlan()
    {
        var websitePart = new DeploymentPlanPart(
            "website",
            "vercel",
            RootDirectory: string.Empty,
            BuildCommand: "npm run build",
            InstallCommand: "npm install",
            OutputDirectory: "dist",
            Framework: null);

        var serverPart = new DeploymentPlanPart(
            "server",
            "railway",
            RootDirectory: string.Empty,
            ServiceDirectory: string.Empty,
            Framework: null);

        var question = new ClarifyingQuestion(
            "Does your app need to run code on a server, or is it just pages?",
            [
                new ClarifyingQuestionOption(
                    "pages",
                    "Just pages",
                    "Static files or a site that doesn't need its own always-on server.",
                    [websitePart]),
                new ClarifyingQuestionOption(
                    "server",
                    "It needs a server",
                    "An API, background jobs, or other code that must keep running.",
                    [serverPart])
            ]);

        return new DeploymentPlan(
            [],
            "low",
            "We couldn't tell exactly what kind of app this is yet.",
            question);
    }

    private static DeploymentPlan BuildAmbiguousFrontendPlan(FrontendBuildProfile websiteProfile)
    {
        var websitePart = ToWebsitePart(websiteProfile, preferCoolify: false);
        var serverPart = new DeploymentPlanPart(
            "server",
            "railway",
            RootDirectory: string.Empty,
            ServiceDirectory: string.Empty,
            Framework: null);

        var question = new ClarifyingQuestion(
            "Does your app need to run code on a server, or is it just pages?",
            [
                new ClarifyingQuestionOption(
                    "pages",
                    "Just pages",
                    "The site can be served as static or hosted pages without a separate backend.",
                    [websitePart]),
                new ClarifyingQuestionOption(
                    "server",
                    "It needs a server",
                    "The app runs server-side code that must stay online.",
                    [serverPart])
            ]);

        return new DeploymentPlan(
            [],
            "low",
            DescribeFrontend(websiteProfile.Framework) + " We want to make sure we host it the right way.",
            question);
    }

    private static bool IsAmbiguousFrontendOnly(FrontendBuildProfile profile) =>
        profile.Framework is "vite" or "react" or "angular";

    private async Task<bool> HasStaticIndexHtmlAsync(
        string accessToken,
        string owner,
        string repo,
        string path,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        var indexPath = string.IsNullOrEmpty(path) ? "index.html" : $"{path}/index.html";
        var content = await _gitHubService.GetFileContentAsync(
            accessToken, owner, repo, indexPath, gitRef, cancellationToken);
        return !string.IsNullOrWhiteSpace(content);
    }

    private async Task<DatabaseRequirementProfile> DetectDatabaseRequirementsAsync(
        string accessToken,
        string owner,
        string repo,
        string serverPath,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        var dockerCompose = await ReadFirstExistingFileAsync(
            accessToken,
            owner,
            repo,
            ["docker-compose.yml", "docker-compose.yaml"],
            gitRef,
            cancellationToken);
        var appsettingsPath = string.IsNullOrEmpty(serverPath)
            ? "appsettings.json"
            : $"{serverPath}/appsettings.json";
        var appsettings = await _gitHubService.GetFileContentAsync(
            accessToken, owner, repo, appsettingsPath, gitRef, cancellationToken);
        var prismaPaths = new List<string> { "prisma/schema.prisma" };
        if (!string.IsNullOrEmpty(serverPath))
        {
            prismaPaths.Add($"{serverPath}/prisma/schema.prisma");
        }

        var prismaSchema = await ReadFirstExistingFileAsync(
            accessToken,
            owner,
            repo,
            prismaPaths,
            gitRef,
            cancellationToken);

        return _databaseRequirementDetector.Detect(dockerCompose, appsettings, prismaSchema);
    }

    private async Task<string?> ReadFirstExistingFileAsync(
        string accessToken,
        string owner,
        string repo,
        IReadOnlyList<string> paths,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            var content = await _gitHubService.GetFileContentAsync(
                accessToken, owner, repo, path, gitRef, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }
        }

        return null;
    }

    public static string BuildPlainSummary(
        FrontendBuildProfile? websiteProfile,
        ServerBuildProfile serverProfile,
        DatabaseRequirementProfile databaseProfile,
        bool hasWebsite,
        bool hasServer,
        bool preferCoolify = false)
    {
        var needsDatabase = databaseProfile.RequiresPostgres || databaseProfile.RequiresRedis;
        var databaseNote = needsDatabase
            ? " We also found it needs a database, so we'll set that up too."
            : string.Empty;

        if (preferCoolify && hasWebsite && hasServer)
        {
            var site = DescribeFrontend(websiteProfile?.Framework);
            var api = DescribeServer(serverProfile.Framework);
            return $"Looks like an app with {site} and {api}. We'll deploy everything to your Coolify server.{databaseNote}";
        }

        if (preferCoolify && hasServer && ShouldUseCoolifyForServer(serverProfile, preferCoolify: true))
        {
            return $"Looks like {DescribeServer(serverProfile.Framework)}. We'll run it on your Coolify server.{databaseNote}";
        }

        if (hasWebsite && hasServer)
        {
            var site = DescribeFrontend(websiteProfile?.Framework);
            var api = DescribeServer(serverProfile.Framework);
            return $"Looks like an app with {site} and {api}. We'll put your site on fast global hosting and your server on a host that keeps it running.{databaseNote}";
        }

        if (hasWebsite && websiteProfile is not null)
        {
            return $"Looks like {DescribeFrontend(websiteProfile.Framework)}. We'll put it on fast global hosting.";
        }

        if (hasServer)
        {
            return $"Looks like {DescribeServer(serverProfile.Framework)}. We'll run it on a server that keeps it online.{databaseNote}";
        }

        return "We'll figure out the best way to put this app live.";
    }

    private static string DescribeFrontend(string? framework) => framework switch
    {
        "angular" => "an Angular website",
        "next" => "a Next.js app",
        "nuxt" => "a Nuxt app",
        "sveltekit" => "a SvelteKit app",
        "astro" => "an Astro site",
        "vite" => "a Vite web app",
        "react" => "a React web app",
        _ => "a website"
    };

    private static string DescribeServer(string? framework) => framework switch
    {
        "dotnet" => "a .NET API",
        "node" => "a Node server",
        "python" => "a Python server",
        "go" => "a Go server",
        "rust" => "a Rust server",
        "docker" => "a containerized service",
        _ => "a server"
    };
}
