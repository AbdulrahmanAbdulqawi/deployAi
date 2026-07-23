using DeployAI.Api.Services;
using DeployAI.Api.Services.DeploymentTemplates;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;

namespace DeployAI.Tests.Services;

public class ClaudeDeploymentPromptsTests
{
    [Fact]
    public void BuildFixPrompt_IncludesContextErrorAndOutputFormat()
    {
        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            "Build failed with 2 errors",
            "error TS2304: Cannot find name 'Foo'.\nerror TS2304: Cannot find name 'Bar'.",
            ["src/app/a.component.ts", "src/app/b.component.ts"],
            true,
            2);

        var prompt = ClaudeDeploymentPrompts.BuildFixPrompt(
            "owner",
            "repo",
            "abc123",
            "Vercel",
            "Angular",
            analysis);

        Assert.Contains("Repository: owner/repo @ abc123", prompt);
        Assert.Contains("Failed provider: Vercel", prompt);
        Assert.Contains("Framework: Angular", prompt);
        Assert.Contains("error TS2304", prompt);
        Assert.Contains("## Build Log", prompt);
        Assert.Contains("## Structured Errors", prompt);
        Assert.Contains("Distinct errors detected: 2", prompt);
        Assert.Contains("Fix ALL errors listed", prompt);
        Assert.Contains("run_command", prompt);
        Assert.Contains("### Verification", prompt);
        Assert.Contains("## What NOT to Do", prompt);
        Assert.Contains("src/app/a.component.ts", prompt);
        Assert.Contains("src/app/b.component.ts", prompt);
        Assert.Contains("write_file", prompt);
        Assert.Contains("read_file", prompt);
        Assert.Contains("submit_deployment_files", prompt);
    }

    [Fact]
    public void BuildFixPrompt_OmitsAgentBuildCommands_WhenDisabled()
    {
        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            "Build failed",
            "error CS0246: type not found",
            ["Program.cs"],
            true,
            1);

        var prompt = ClaudeDeploymentPrompts.BuildFixPrompt(
            "owner",
            "repo",
            "abc123",
            "railway",
            "AspNetCore",
            analysis,
            allowAgentBuildCommands: false);

        Assert.Contains("Local build verification is disabled", prompt);
        Assert.Contains("Do NOT use run_command", prompt);
        Assert.DoesNotContain("## Verification Commands", prompt);
        Assert.DoesNotContain("- run_command:", prompt);
    }

    [Fact]
    public void BuildFixPrompt_IncludesVerificationCommands_WhenPlanProvided()
    {
        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            "Build failed",
            "error CS0246: type not found",
            ["ConvertControllerApisToPostman/Program.cs"],
            true,
            1);
        var plan = new FixBuildPlan(
            "ConvertControllerApisToPostman",
            null,
            "dotnet build \"ConvertControllerApisToPostman.csproj\"");

        var prompt = ClaudeDeploymentPrompts.BuildFixPrompt(
            "owner",
            "repo",
            "abc123",
            "railway",
            "AspNetCore",
            analysis,
            verificationPlan: plan,
            allowAgentBuildCommands: true);

        Assert.Contains("## Verification Commands", prompt);
        Assert.Contains("dotnet build \"ConvertControllerApisToPostman.csproj\"", prompt);
        Assert.Contains("Do not run publish, Release", prompt);
        Assert.Contains("dotnet build** only", prompt);
        Assert.DoesNotContain("dotnet publish", prompt.Split("## Verification Commands")[0]);
    }

    [Fact]
    public void BuildMissingFilesPrompt_IncludesContextPlanGapsAndTools()
    {
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "vercel", RootDirectory: "client", Framework: "Angular"),
            new("server", "railway", ServiceDirectory: "src/Api", Framework: "AspNetCore", DockerfilePath: "src/Api/Dockerfile")
        };

        var missing = new List<MissingDeploymentFile>
        {
            new("client/vercel.json", "SPA routing config missing", DeploymentFileSeverity.Blocking),
            new("src/Api/Program.cs", "CORS missing", DeploymentFileSeverity.Recommended)
        };

        var prompt = ClaudeDeploymentPrompts.BuildMissingFilesPrompt(
            "owner",
            "repo",
            "main",
            parts,
            missing);

        Assert.Contains("You are a deployment engineer completing deployment setup for a repository.", prompt);
        Assert.Contains("Repository: owner/repo @ main", prompt);
        Assert.Contains("## Deployment Plan", prompt);
        Assert.Contains("\"ProviderName\": \"vercel\"", prompt);
        Assert.Contains("\"ProviderName\": \"railway\"", prompt);
        Assert.Contains("[Blocking] client/vercel.json", prompt);
        Assert.Contains("[Recommended] src/Api/Program.cs", prompt);
        Assert.Contains("github_read_file", prompt);
        Assert.Contains("submit_deployment_files", prompt);
        Assert.Contains("## Build Validation Step", prompt);
        Assert.Contains("## What NOT to Do", prompt);
        Assert.Contains("### Split-Origin Angular (Vercel + Railway)", prompt);
    }

    [Fact]
    public void BuildMissingFilesPrompt_IncludesReferenceTemplates_WhenProvided()
    {
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "vercel", RootDirectory: "client", Framework: "Angular"),
            new("server", "railway", ServiceDirectory: "src/Api", Framework: "AspNetCore")
        };
        var missing = new List<MissingDeploymentFile>
        {
            new("client/vercel.json", "SPA routing config missing", DeploymentFileSeverity.Blocking)
        };
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var referenceTemplates = resolver.ResolveForGaps(parts, missing);

        var prompt = ClaudeDeploymentPrompts.BuildMissingFilesPrompt(
            "owner",
            "repo",
            "main",
            parts,
            missing,
            referenceTemplates,
            []);

        Assert.Contains("## Reference Templates", prompt);
        Assert.Contains("client/vercel.json", prompt);
        Assert.DoesNotContain("### Split-Origin Angular (Vercel + Railway)", prompt);
    }

    [Fact]
    public void BuildMissingFilesPrompt_DescribesSingleOriginCompose_ForAngularDotnetOnCoolify()
    {
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "coolify", RootDirectory: "client", Framework: "Angular"),
            new("server", "coolify", ServiceDirectory: "src/Api", Framework: "AspNetCore")
        };
        var missing = new List<MissingDeploymentFile>
        {
            new("docker-compose.coolify.yml", "missing", DeploymentFileSeverity.Blocking)
        };
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var referenceTemplates = resolver.ResolveForGaps(parts, missing);

        var prompt = ClaudeDeploymentPrompts.BuildMissingFilesPrompt(
            "owner",
            "repo",
            "main",
            parts,
            missing,
            referenceTemplates,
            []);

        Assert.Contains("single-origin Docker Compose", prompt);
        Assert.Contains("## Reference Templates", prompt);

        // The split-origin instructions must not leak in: telling Claude to inject an API base
        // URL into a single-origin app would break the proxy that makes it work.
        Assert.DoesNotContain("Coolify: set DEPLOYAI_API_URL", prompt);
        Assert.DoesNotContain("client/vercel.json", prompt);
    }

    [Fact]
    public void BuildFixPrompt_IncludesCoolifyReferenceTemplates_WhenProvided()
    {
        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            "Build failed",
            "nginx.conf missing",
            ["client/nginx.conf"],
            true);
        var parts = DeploymentFixPlanBuilder.BuildSyntheticSplitOriginParts(
            ProviderNameValues.Coolify,
            "angular",
            new DeployTargetConfig { Role = "website", RootDirectory = "client", Framework = "angular" });
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var references = resolver.ResolveForSplitOriginFix(parts, analysis.ReferencedFiles);

        var prompt = ClaudeDeploymentPrompts.BuildFixPrompt(
            "owner",
            "repo",
            "abc123",
            ProviderNameValues.Coolify,
            "Angular",
            analysis,
            referenceTemplates: references);

        Assert.Contains("## Reference Templates", prompt);
        Assert.Contains("nginx.conf", prompt);
        Assert.DoesNotContain("client/vercel.json", prompt);
    }

    [Fact]
    public void BuildFixPrompt_IncludesReferenceTemplates_WhenProvided()
    {
        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            "Build failed",
            "vercel.json rewrite error",
            ["client/vercel.json"],
            true,
            1);
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "vercel", RootDirectory: "client", Framework: "Angular"),
            new("server", "railway", ServiceDirectory: "src/Api", Framework: "AspNetCore")
        };
        var references = resolver.ResolveForSplitOriginFix(parts, analysis.ReferencedFiles);

        var prompt = ClaudeDeploymentPrompts.BuildFixPrompt(
            "owner",
            "repo",
            "abc123",
            "Vercel",
            "Angular",
            analysis,
            repoContext: null,
            references);

        Assert.Contains("## Reference Templates", prompt);
        Assert.Contains("client/vercel.json", prompt);
    }

    [Fact]
    public void BuildMissingFilesPrompt_DescribesArchitectureForSinglePartPlan()
    {
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "vercel", RootDirectory: "client", Framework: "NextJs")
        };

        var missing = new List<MissingDeploymentFile>
        {
            new("client/vercel.json", "Host config missing", DeploymentFileSeverity.Blocking)
        };

        var prompt = ClaudeDeploymentPrompts.BuildMissingFilesPrompt(
            "owner",
            "repo",
            "main",
            parts,
            missing);

        Assert.Contains("single-part deployment", prompt);
        Assert.DoesNotContain("Split-origin checklist", prompt);
    }

    [Fact]
    public void BuildVerificationFixPrompt_IncludesHealthCheckContextAndOutputDirectoryHints()
    {
        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            "Website returned 404",
            "Health check: Website homepage (website.reachable)\nStatus: failed",
            ["vercel.json", "angular.json"],
            true);

        var verificationContext = new DeploymentVerificationFixContext(
            "website.reachable",
            "website",
            "Website homepage",
            "Website returned 404",
            "https://app.vercel.app/",
            "https://app.vercel.app/",
            null);

        var targetConfig = DeployTargetConfig.Parse("""{"framework":"angular","outputDirectory":"dist/app/browser"}""");

        var prompt = ClaudeDeploymentPrompts.BuildVerificationFixPrompt(
            "owner",
            "repo",
            "main",
            "vercel",
            "angular",
            analysis,
            verificationContext,
            targetConfig);

        Assert.Contains("live deployment health check failure", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("website.reachable", prompt);
        Assert.Contains("dist/app/browser", prompt);
        Assert.Contains("Local build verification is disabled", prompt);
    }
}
