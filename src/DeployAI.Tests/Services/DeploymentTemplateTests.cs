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
                "process.env.DEPLOYAI_API_URL ?? process.env.API_BASE_URL",
                "Api.Controllers"));

        Assert.Contains("dockerfilePath = \"src/Api/Dockerfile\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVariables_ApiEnvKeysExpression_MatchesCanonicalKeyList()
    {
        var website = new DeploymentPlanPart("website", "vercel", RootDirectory: "client", Framework: "angular");
        var server = new DeploymentPlanPart("server", "railway", ServiceDirectory: "src/Api", Framework: "aspnetcore");

        var variables = DeploymentTemplateRenderer.BuildVariables(website, server);
        var expectedKeys = CrossProviderUrlWiring.ResolveApiEnvKeys("angular");

        // Guards against write-api-env.mjs.tpl's rendered expression silently drifting from
        // the canonical env key list in CrossProviderUrlWiring.ResolveApiEnvKeys.
        Assert.Equal(
            string.Join(" ?? ", expectedKeys.Select(key => $"process.env.{key}")),
            variables.ApiEnvKeysExpression);
        Assert.Equal(string.Join(" / ", expectedKeys), variables.ApiEnvKeysList);
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

    private static readonly List<DeploymentPlanPart> SplitOriginParts =
    [
        new("website", "vercel", RootDirectory: "client", Framework: "Angular"),
        new("server", "railway", ServiceDirectory: "src/Api", Framework: "AspNetCore")
    ];

    [Fact]
    public void ScaffoldMissingFiles_PatchesAuthService_RewritesPathAndAddsWithCredentials()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var scaffolder = new DeploymentFileScaffolder(resolver);
        var missing = new List<MissingDeploymentFile>
        {
            new("client/src/app/core/services/auth.service.ts", "wrong path", DeploymentFileSeverity.Blocking)
        };
        var existing = new Dictionary<string, string?>
        {
            ["client/src/app/core/services/auth.service.ts"] =
                "login(body: LoginRequest) {\n  return this.http.post<AuthResponse>('/api/Auth/login', body);\n}"
        };

        var generated = scaffolder.ScaffoldMissingFiles(SplitOriginParts, missing, existing);

        var file = Assert.Single(generated);
        Assert.Contains("/api/v1/auth/login", file.Content, StringComparison.Ordinal);
        Assert.Contains("withCredentials: true", file.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaffoldMissingFiles_PatchesSignalRService_RewritesRelativeHubUrl()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var scaffolder = new DeploymentFileScaffolder(resolver);
        var missing = new List<MissingDeploymentFile>
        {
            new("client/src/app/core/services/signalr.service.ts", "relative hub url", DeploymentFileSeverity.Recommended)
        };
        var existing = new Dictionary<string, string?>
        {
            ["client/src/app/core/services/signalr.service.ts"] =
                "const hubUrl = '/hubs/notifications';"
        };

        var generated = scaffolder.ScaffoldMissingFiles(SplitOriginParts, missing, existing);

        var file = Assert.Single(generated);
        Assert.Contains("environment.apiBaseUrl", file.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("// TODO:", file.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaffoldMissingFiles_PatchesSignalRService_FallsBackToTodo_WhenNoRelativeHubUrlLiteralFound()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var scaffolder = new DeploymentFileScaffolder(resolver);
        var missing = new List<MissingDeploymentFile>
        {
            new("client/src/app/core/services/signalr.service.ts", "no hub literal", DeploymentFileSeverity.Recommended)
        };
        var existing = new Dictionary<string, string?>
        {
            ["client/src/app/core/services/signalr.service.ts"] =
                "const hubUrl = buildHubUrl();"
        };

        var generated = scaffolder.ScaffoldMissingFiles(SplitOriginParts, missing, existing);

        var file = Assert.Single(generated);
        Assert.Contains("// TODO:", file.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaffoldMissingFiles_PatchesAuthController_SetsSameSiteNoneAndSecure_OnExistingCookieOptions()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var scaffolder = new DeploymentFileScaffolder(resolver);
        var missing = new List<MissingDeploymentFile>
        {
            new("src/Api/Controllers/AuthController.cs", "missing SameSite", DeploymentFileSeverity.Recommended)
        };
        var existing = new Dictionary<string, string?>
        {
            ["src/Api/Controllers/AuthController.cs"] =
                "Response.Cookies.Append(\"refreshToken\", token, new CookieOptions { HttpOnly = true });"
        };

        var generated = scaffolder.ScaffoldMissingFiles(SplitOriginParts, missing, existing);

        var file = Assert.Single(generated);
        Assert.Contains("SameSiteMode.None", file.Content, StringComparison.Ordinal);
        Assert.Contains("Secure = true", file.Content, StringComparison.Ordinal);
        Assert.Contains("HttpOnly = true", file.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaffoldMissingFiles_PatchesAuthController_FallsBackToTodo_WhenNoCookieOptionsFound()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var scaffolder = new DeploymentFileScaffolder(resolver);
        var missing = new List<MissingDeploymentFile>
        {
            new("src/Api/Controllers/AuthController.cs", "missing SameSite", DeploymentFileSeverity.Recommended)
        };
        var existing = new Dictionary<string, string?>
        {
            ["src/Api/Controllers/AuthController.cs"] =
                "[HttpPost(\"refresh\")]\npublic IActionResult Refresh() => Ok();"
        };

        var generated = scaffolder.ScaffoldMissingFiles(SplitOriginParts, missing, existing);

        var file = Assert.Single(generated);
        Assert.Contains("// TODO:", file.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaffoldMissingFiles_GreenfieldAuthController_DerivesNamespaceFromServerRoot_NotToolName()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var scaffolder = new DeploymentFileScaffolder(resolver);
        var missing = new List<MissingDeploymentFile>
        {
            new("src/Api/Controllers/AuthController.cs", "missing file", DeploymentFileSeverity.Recommended)
        };

        var generated = scaffolder.ScaffoldMissingFiles(SplitOriginParts, missing);

        var file = Assert.Single(generated);
        Assert.Contains("namespace Api.Controllers;", file.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("DeployAI.Generated", file.Content, StringComparison.Ordinal);
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

    [Fact]
    public void MergeFiles_RejectsAiOverwrite_OfDeterministicPathNotForwardedToAi()
    {
        var deterministic = new[]
        {
            new GeneratedDeploymentFile("client/vercel.json", "{ \"version\": 2, \"deterministic\": true }")
        };
        var ai = new[]
        {
            // Claude wasn't asked to touch vercel.json (not in allowedAiOverwritePaths) but its
            // free-roaming tool-using agent submitted a file at that path anyway.
            new GeneratedDeploymentFile("client/vercel.json", "{ \"version\": 2, \"ai\": true }"),
            new GeneratedDeploymentFile("railway.toml", "[build]")
        };
        var allowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "railway.toml" };

        var merged = HybridDeploymentFileGenerator.MergeFiles(deterministic, ai, allowedPaths);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, file => file.Path == "client/vercel.json" && file.Content.Contains("\"deterministic\": true"));
        Assert.Contains(merged, file => file.Path == "railway.toml" && file.Content == "[build]");
    }

    [Fact]
    public void MergeFiles_AllowsAiOverwrite_OfDeterministicPathForwardedToAi()
    {
        var deterministic = new[]
        {
            new GeneratedDeploymentFile("client/src/app/core/services/signalr.service.ts", "// TODO: unresolved")
        };
        var ai = new[]
        {
            new GeneratedDeploymentFile("client/src/app/core/services/signalr.service.ts", "environment.apiBaseUrl fix")
        };
        var allowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "client/src/app/core/services/signalr.service.ts"
        };

        var merged = HybridDeploymentFileGenerator.MergeFiles(deterministic, ai, allowedPaths);

        var file = Assert.Single(merged);
        Assert.Equal("environment.apiBaseUrl fix", file.Content);
    }
}
