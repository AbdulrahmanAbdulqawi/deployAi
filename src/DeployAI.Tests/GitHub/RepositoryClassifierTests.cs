using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Infrastructure.GitHub;
using Moq;

namespace DeployAI.Tests.GitHub;

public class RepositoryClassifierTests
{
    private readonly Mock<IGitHubService> _gitHub = new();
    private readonly FrontendBuildDetector _frontendDetector = new();
    private readonly ServerBuildDetector _serverDetector = new();
    private readonly DatabaseRequirementDetector _databaseDetector = new();

    [Fact]
    public async Task ClassifyAsync_StaticSite_DefaultsToCoolify()
    {
        SetupRootFiles(["index.html"]);
        SetupFile("index.html", "<html></html>");

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Single(plan.Parts);
        Assert.Equal("website", plan.Parts[0].Role);
        Assert.Equal(ProviderNameValues.Coolify, plan.Parts[0].ProviderName);
    }

    [Fact]
    public async Task ClassifyAsync_StaticSite_UsesVercel_WhenLegacyPathRequested()
    {
        SetupRootFiles(["index.html"]);
        SetupFile("index.html", "<html></html>");

        var plan = await ClassifyAsync(preferVercelRailway: true);

        Assert.Single(plan.Parts);
        Assert.Equal(ProviderNameValues.Vercel, plan.Parts[0].ProviderName);
        Assert.Contains("global hosting", plan.PlainSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassifyAsync_DotNetServer_DefaultsToCoolify()
    {
        SetupRootContents(["My.Api"]);
        SetupDirectoryContents("My.Api", ["My.Api.csproj"]);
        SetupFile("My.Api/My.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Single(plan.Parts);
        Assert.Equal("server", plan.Parts[0].Role);
        Assert.Equal(ProviderNameValues.Coolify, plan.Parts[0].ProviderName);
        Assert.Equal("dotnet", plan.Parts[0].Framework);
    }

    [Fact]
    public async Task ClassifyAsync_DotNetServer_UsesRailway_WhenLegacyPathRequested()
    {
        SetupRootContents(["My.Api"]);
        SetupDirectoryContents("My.Api", ["My.Api.csproj"]);
        SetupFile("My.Api/My.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        var plan = await ClassifyAsync(preferVercelRailway: true);

        Assert.Single(plan.Parts);
        Assert.Equal(ProviderNameValues.Railway, plan.Parts[0].ProviderName);
    }

    private void SetupMonorepo()
    {
        SetupRootContents(["client", "My.Api"]);
        SetupDirectoryContents("client", ["angular.json", "package.json"]);
        SetupDirectoryContents("My.Api", ["My.Api.csproj"]);
        SetupFile("client/angular.json", """
            {
              "projects": {
                "client": {
                  "architect": {
                    "build": {
                      "builder": "@angular-devkit/build-angular:application",
                      "options": { "outputPath": "dist/client" }
                    }
                  }
                }
              }
            }
            """);
        SetupFile("client/package.json", """{ "dependencies": { "@angular/core": "19.0.0" } }""");
        SetupFile("My.Api/My.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
    }

    [Fact]
    // One server hosting both halves means one compose file behind one origin — not two
    // separately-addressed Coolify apps wired back together with CORS.
    public async Task ClassifyAsync_Monorepo_DefaultsToSingleOriginCompose()
    {
        SetupMonorepo();

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Equal(2, plan.Parts.Count);
        Assert.Contains(plan.Parts, part => part.Role == "website" && part.ProviderName == ProviderNameValues.Coolify);
        Assert.Contains(plan.Parts, part => part.Role == "server" && part.ProviderName == ProviderNameValues.Coolify);
        Assert.Equal(DeploymentPlanKind.CoolifyCompose, plan.PlanKind);
    }

    [Fact]
    public async Task ClassifyAsync_Monorepo_SplitsAcrossVercelAndRailway_WhenLegacyPathRequested()
    {
        SetupMonorepo();

        var plan = await ClassifyAsync(preferVercelRailway: true);

        Assert.Equal(2, plan.Parts.Count);
        Assert.Contains(plan.Parts, part => part.Role == "website" && part.ProviderName == ProviderNameValues.Vercel);
        Assert.Contains(plan.Parts, part => part.Role == "server" && part.ProviderName == ProviderNameValues.Railway);
        Assert.Equal(DeploymentPlanKind.Default, plan.PlanKind);
    }

    [Fact]
    public async Task ClassifyAsync_DeployAiLayout_ReturnsClientWebsiteAndNestedDotnetApi()
    {
        SetupRootContents(["client", "src"]);
        SetupDirectoryContents("client", ["angular.json", "package.json"]);
        SetupDirectorySubdirectories("src", ["DeployAI.Api", "DeployAI.Core"]);
        SetupDirectoryContents("src/DeployAI.Api", ["DeployAI.Api.csproj"]);
        SetupDirectoryContents("src/DeployAI.Core", ["DeployAI.Core.csproj"]);
        SetupFile("client/angular.json", """
            {
              "projects": {
                "client": {
                  "architect": {
                    "build": {
                      "builder": "@angular-devkit/build-angular:application",
                      "options": { "outputPath": "dist/client" }
                    }
                  }
                }
              }
            }
            """);
        SetupFile("client/package.json", """
            {
              "dependencies": { "@angular/core": "18.0.0" },
              "scripts": { "start": "ng serve", "build": "ng build" }
            }
            """);
        SetupFile("src/DeployAI.Api/DeployAI.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <ProjectReference Include="..\DeployAI.Core\DeployAI.Core.csproj" />
              </ItemGroup>
            </Project>
            """);
        SetupFile("src/DeployAI.Core/DeployAI.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Equal(2, plan.Parts.Count);

        var website = plan.Parts.Single(part => part.Role == "website");
        Assert.Equal(ProviderNameValues.Coolify, website.ProviderName);
        Assert.Equal("client", website.RootDirectory);
        Assert.Equal("angular", website.Framework);

        var server = plan.Parts.Single(part => part.Role == "server");
        Assert.Equal(ProviderNameValues.Coolify, server.ProviderName);
        Assert.Equal("src", server.RootDirectory);
        Assert.Equal("src/DeployAI.Api", server.ServiceDirectory);
        Assert.Equal("dotnet", server.Framework);
        Assert.Equal("dotnet publish DeployAI.Api/DeployAI.Api.csproj -c Release -o out", server.BuildCommand);
        Assert.Equal("dotnet out/DeployAI.Api.dll", server.StartCommand);
    }

    [Fact]
    // yemenConnect's layout: a Next.js app two levels deep (apps/web) and a .NET API three
    // levels deep (backend/src/YemenHub.Api). Detection previously collapsed to low-confidence
    // because the scan stopped one level below the root.
    public async Task ClassifyAsync_NestedMonorepo_DetectsNextAndDeeplyNestedDotnet()
    {
        SetupRootContents(["apps", "backend", "docs", "infra", "scripts"]);
        SetupDirectorySubdirectories("apps", ["web"]);
        SetupDirectoryContents("apps/web", ["next.config.ts", "package.json"]);
        SetupFile("apps/web/package.json", """
            {
              "dependencies": { "next": "16.2.10", "react": "19.2.4" },
              "scripts": { "build": "next build", "start": "next start" }
            }
            """);

        SetupDirectorySubdirectories("backend", ["src", "tests"]);
        SetupDirectorySubdirectories("backend/src", ["YemenHub.Api", "YemenHub.Modules", "YemenHub.Persistence"]);
        SetupDirectoryContents("backend/src/YemenHub.Api", ["YemenHub.Api.csproj"]);
        // A modular monolith: the API csproj references sibling projects, so the build context
        // is the parent (backend/src) and the publish path is relative to it.
        SetupFile("backend/src/YemenHub.Api/YemenHub.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <ProjectReference Include="..\YemenHub.Modules\YemenHub.Modules.csproj" />
                <ProjectReference Include="..\YemenHub.Persistence\YemenHub.Persistence.csproj" />
              </ItemGroup>
            </Project>
            """);

        // Never reached (apps/backend resolve first), but kept empty so a ranking change can't NPE.
        SetupDirectorySubdirectories("docs", []);
        SetupDirectorySubdirectories("infra", []);
        SetupDirectorySubdirectories("scripts", []);

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Equal(2, plan.Parts.Count);

        var website = plan.Parts.Single(part => part.Role == "website");
        Assert.Equal("next", website.Framework);
        Assert.Equal("apps/web", website.RootDirectory);

        var server = plan.Parts.Single(part => part.Role == "server");
        Assert.Equal("dotnet", server.Framework);
        Assert.Equal("backend/src", server.RootDirectory);
        Assert.Equal("backend/src/YemenHub.Api", server.ServiceDirectory);
        Assert.Equal("dotnet publish YemenHub.Api/YemenHub.Api.csproj -c Release -o out", server.BuildCommand);
    }

    [Fact]
    public async Task ClassifyAsync_ServerWithPostgres_IncludesDatabasePart()
    {
        SetupRootContents(["api"]);
        SetupDirectoryContents("api", ["package.json", "appsettings.json"]);
        SetupFile("api/package.json", """{ "scripts": { "start": "node index.js" } }""");
        SetupFile("api/appsettings.json", """
            {
              "ConnectionStrings": {
                "DefaultConnection": "Host=localhost;Database=app;"
              }
            }
            """);
        SetupFile("docker-compose.yml", """
            services:
              postgres:
                image: postgres:16
            """);

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Contains(plan.Parts, part => part.Role == "server");
        Assert.Contains(plan.Parts, part => part.Role == "database" && part.DatabaseEngine == "postgres");
    }

    [Fact]
    public async Task ClassifyAsync_EmptyRepo_ReturnsLowConfidenceQuestion()
    {
        SetupRootContents(["docs"]);
        SetupDirectoryContents("docs", ["README.md"]);

        var plan = await ClassifyAsync();

        Assert.Equal("low", plan.Confidence);
        Assert.Empty(plan.Parts);
        Assert.NotNull(plan.ClarifyingQuestion);
        Assert.Equal(2, plan.ClarifyingQuestion!.Options.Count);
    }

    [Fact]
    public async Task ClassifyAsync_AmbiguousReactApp_ReturnsLowConfidenceQuestion()
    {
        SetupRootFiles(["package.json"]);
        SetupDirectoryContents(string.Empty, ["package.json"]);
        SetupFile("package.json", """
            {
              "dependencies": {
                "react": "18.0.0",
                "vite": "5.0.0"
              },
              "scripts": {
                "build": "vite build"
              }
            }
            """);

        var plan = await ClassifyAsync();

        Assert.Equal("low", plan.Confidence);
        Assert.NotNull(plan.ClarifyingQuestion);
    }

    [Fact]
    public async Task ClassifyAsync_ReadsAComposeFileThatSitsWithTheBackend()
    {
        // docker-compose.yml was read from the repository root and nowhere else, so a repo that
        // keeps it beside the backend -- the usual shape once a monorepo has more than one service
        // -- produced a plan offering no database at all, and a user who cannot know they need one
        // was left to ask for it.
        SetupRootContents(["backend"]);
        SetupDirectoryContents("backend", ["My.Api.csproj", "docker-compose.yml"]);
        SetupFile("backend/My.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        SetupFile("backend/docker-compose.yml", """
            services:
              db:
                image: postgres:16
            """);

        var plan = await ClassifyAsync();

        Assert.Contains(plan.Parts, part => part.Role == "database" && part.DatabaseEngine == "postgres");
    }

    [Fact]
    public void BuildPlainSummary_Monorepo_MentionsWebsiteAndServer()
    {
        var website = new DeployAI.Core.Deployments.FrontendBuildProfile("client", "npm run build", "npm install", "dist/client/browser", "angular");
        var server = new DeployAI.Core.Deployments.ServerBuildProfile("api", null, null, null, "dotnet");
        var database = new DeployAI.Core.Deployments.DatabaseRequirementProfile(true, false, ["DefaultConnection"]);

        var summary = RepositoryClassifier.BuildPlainSummary(website, server, database, true, true);

        Assert.Contains("site on fast global hosting", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("database", summary, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<DeployAI.Core.Deployments.DeploymentPlan> ClassifyAsync(bool preferVercelRailway = false)
    {
        // The real resolver over the same mocked GitHub service, so these tests exercise the
        // directory resolution the classifier now shares rather than a stub of it.
        var layout = new RepositoryLayoutResolver(_gitHub.Object);
        var websiteDiscovery = new WebsiteBuildProfileDiscovery(_gitHub.Object, _frontendDetector);
        var serverDiscovery = new ServerBuildProfileDiscovery(_gitHub.Object, _serverDetector, layout);
        var classifier = new RepositoryClassifier(
            _gitHub.Object,
            websiteDiscovery,
            serverDiscovery,
            _databaseDetector,
            layout,
            layout);

        return await classifier.ClassifyAsync(
            "token",
            "owner",
            "repo",
            "main",
            new RepositoryClassificationOptions(preferVercelRailway),
            CancellationToken.None);
    }

    [Fact]
    public async Task ClassifyAsync_Monorepo_SummaryMentionsCoolify()
    {
        SetupMonorepo();

        var plan = await ClassifyAsync();

        Assert.Contains("Coolify", plan.PlainSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassifyAsync_DockerServer_ResolvesToCoolifySingle()
    {
        SetupRootContents(["api"]);
        SetupDirectoryContents("api", ["Dockerfile"]);
        SetupFile("api/Dockerfile", "FROM node:20");

        var plan = await ClassifyAsync();

        Assert.Equal(DeploymentPlanKind.CoolifySingle, plan.PlanKind);
        Assert.Single(plan.Parts);
        Assert.Equal(ProviderNameValues.Coolify, plan.Parts[0].ProviderName);
    }

    /// <summary>
    /// A Dockerfile routes the server to Coolify even on the legacy path, because neither
    /// Vercel nor Railway is the right home for it. The plan kind must reflect what was
    /// actually chosen — it previously short-circuited to Default here.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_DockerServer_StillResolvesToCoolify_OnLegacyPath()
    {
        SetupRootContents(["api"]);
        SetupDirectoryContents("api", ["Dockerfile"]);
        SetupFile("api/Dockerfile", "FROM node:20");

        var plan = await ClassifyAsync(preferVercelRailway: true);

        Assert.Single(plan.Parts);
        Assert.Equal(ProviderNameValues.Coolify, plan.Parts[0].ProviderName);
        Assert.Equal(DeploymentPlanKind.CoolifySingle, plan.PlanKind);
    }

    private void SetupRootContents(IReadOnlyList<string> entries)
    {
        var items = entries
            .Select(name => new GitHubContentItem(name, name, "dir"))
            .ToList();

        _gitHub.Setup(service => service.ListAllContentsAsync(
                "token", "owner", "repo", string.Empty, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
    }

    private void SetupRootFiles(IReadOnlyList<string> fileNames)
    {
        var items = fileNames
            .Select(name => new GitHubContentItem(name, name, "file"))
            .ToList();

        _gitHub.Setup(service => service.ListAllContentsAsync(
                "token", "owner", "repo", string.Empty, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
    }

    private void SetupDirectoryContents(string path, IReadOnlyList<string> fileNames)
    {
        var normalizedPath = string.IsNullOrEmpty(path) ? string.Empty : path.Trim('/');
        var items = fileNames
            .Select(name => new GitHubContentItem(
                name,
                string.IsNullOrEmpty(normalizedPath) ? name : $"{normalizedPath}/{name}",
                "file"))
            .ToList();

        _gitHub.Setup(service => service.ListAllContentsAsync(
                "token", "owner", "repo", normalizedPath, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
    }

    private void SetupDirectorySubdirectories(string path, IReadOnlyList<string> directoryNames)
    {
        var normalizedPath = string.IsNullOrEmpty(path) ? string.Empty : path.Trim('/');
        var items = directoryNames
            .Select(name => new GitHubContentItem(
                name,
                string.IsNullOrEmpty(normalizedPath) ? name : $"{normalizedPath}/{name}",
                "dir"))
            .ToList();

        _gitHub.Setup(service => service.ListAllContentsAsync(
                "token", "owner", "repo", normalizedPath, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
    }

    private void SetupFile(string path, string content)
    {
        _gitHub.Setup(service => service.GetFileContentAsync(
                "token", "owner", "repo", path, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
    }
}
