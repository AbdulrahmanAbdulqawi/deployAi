using DeployAI.Api.Services;
using DeployAI.Api.Services.DeploymentTemplates;
using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

public class DeploymentTemplateCatalogTests
{
    [Fact]
    public void Catalog_LoadsPhase1Templates()
    {
        var catalog = new DeploymentTemplateCatalog();

        Assert.NotEmpty(catalog.Scenarios);
        Assert.Contains(
            catalog.Templates,
            template => template.Id == "split-origin.angular.vercel.railway.vercel-json");
    }

    [Fact]
    public void ReadTemplateContent_RendersPlaceholders()
    {
        var catalog = new DeploymentTemplateCatalog();
        var definition = catalog.FindTemplateById("split-origin.angular.vercel.railway.railway-toml");
        Assert.NotNull(definition);

        var raw = catalog.ReadTemplateContent(definition.ResourcePath);
        var rendered = DeploymentTemplateRenderer.Render(
            raw,
            new DeploymentTemplateVariables(
                "client",
                "client/",
                "src/Api",
                "src/Api/Dockerfile",
                "dist/app/browser",
                "app",
                "npm ci && node scripts/write-api-env.mjs && npm run build",
                "DEPLOYAI_API_URL / API_BASE_URL",
                "process.env.DEPLOYAI_API_URL ?? process.env.API_BASE_URL"));

        Assert.Contains("dockerfilePath = \"src/Api/Dockerfile\"", rendered, StringComparison.Ordinal);
    }
}

public class DeploymentTemplateResolverTests
{
    [Fact]
    public void ResolveForGaps_MapsBlockingGapsToTemplates()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "vercel", RootDirectory: "client", Framework: "Angular"),
            new("server", "railway", ServiceDirectory: "src/Api", Framework: "AspNetCore", DockerfilePath: "src/Api/Dockerfile")
        };
        var missing = new List<MissingDeploymentFile>
        {
            new("client/vercel.json", "missing", DeploymentFileSeverity.Blocking),
            new("src/Api/Program.cs", "CORS missing", DeploymentFileSeverity.Recommended)
        };

        var resolved = resolver.ResolveForGaps(parts, missing);

        Assert.Contains(resolved, template => template.TemplateId.Contains("vercel-json"));
        Assert.DoesNotContain(resolved, template => template.TargetPath.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveForGaps_OmitsPatchTemplates_WhenExistingContentMissing()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "vercel", RootDirectory: "client", Framework: "Angular"),
            new("server", "railway", ServiceDirectory: "src/Api", Framework: "AspNetCore")
        };
        var missing = new List<MissingDeploymentFile>
        {
            new("src/Api/Program.cs", "CORS missing", DeploymentFileSeverity.Recommended)
        };

        var resolved = resolver.ResolveForGaps(parts, missing, existingFilesByPath: null);

        Assert.DoesNotContain(resolved, template => template.TargetPath.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveForGaps_IncludesPatchTemplates_WhenExistingContentPresent()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "vercel", RootDirectory: "client", Framework: "Angular"),
            new("server", "railway", ServiceDirectory: "src/Api", Framework: "AspNetCore")
        };
        var missing = new List<MissingDeploymentFile>
        {
            new("src/Api/Program.cs", "CORS missing", DeploymentFileSeverity.Recommended)
        };
        var existing = new Dictionary<string, string?>
        {
            ["src/Api/Program.cs"] = "var builder = WebApplication.CreateBuilder(args);"
        };

        var resolved = resolver.ResolveForGaps(parts, missing, existing);

        Assert.Contains(resolved, template => template.TargetPath == "src/Api/Program.cs");
    }
}

public class DeploymentFileScaffolderIntegrationTests
{
    [Fact]
    public void ScaffoldMissingFiles_GeneratesSplitOriginFiles_FromTemplates()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var scaffolder = new DeploymentFileScaffolder(resolver);
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "vercel", RootDirectory: "client", Framework: "Angular", OutputDirectory: "dist/app/browser"),
            new("server", "railway", ServiceDirectory: "src/Api", Framework: "AspNetCore", DockerfilePath: "src/Api/Dockerfile")
        };
        var missing = new List<MissingDeploymentFile>
        {
            new("railway.toml", "missing", DeploymentFileSeverity.Blocking),
            new("client/vercel.json", "missing", DeploymentFileSeverity.Blocking),
            new("client/scripts/write-api-env.mjs", "missing", DeploymentFileSeverity.Blocking),
            new("client/src/app/core/interceptors/api-base.interceptor.ts", "missing", DeploymentFileSeverity.Blocking)
        };

        var generated = scaffolder.ScaffoldMissingFiles(parts, missing);

        Assert.Equal(4, generated.Count);
        Assert.Contains(generated, file => file.Path == "railway.toml" && file.Content.Contains("src/Api/Dockerfile"));
        Assert.Contains(generated, file => file.Path == "client/vercel.json" && !file.Content.Contains("/api"));
        Assert.Contains(generated, file => file.Path == "client/scripts/write-api-env.mjs" && file.Content.Contains("process.env.DEPLOYAI_API_URL"));
        Assert.Contains(generated, file => file.Path.EndsWith("api-base.interceptor.ts") && file.Content.Contains("apiBaseInterceptor"));
    }
}

public class HybridDeploymentFileGeneratorTests
{
    [Fact]
    public void MergeFiles_PrefersAiOutput_OnSamePath()
    {
        var deterministic = new[]
        {
            new GeneratedDeploymentFile("client/vercel.json", "{ \"version\": 2, \"deterministic\": true }")
        };
        var ai = new[]
        {
            new GeneratedDeploymentFile("client/vercel.json", "{ \"version\": 2, \"ai\": true }"),
            new GeneratedDeploymentFile("railway.toml", "[build]")
        };

        var merged = HybridDeploymentFileGenerator.MergeFiles(deterministic, ai);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, file => file.Path == "client/vercel.json" && file.Content.Contains("\"ai\": true"));
        Assert.Contains(merged, file => file.Path == "railway.toml");
    }
}
