using DeployAI.Api.Services;
using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

public class SplitOriginReadinessEvaluatorTests
{
    [Fact]
    public void Evaluate_FlagsMissingAngularJson_AsBlocking()
    {
        var website = new DeploymentPlanPart("website", "vercel", "client", null, null, null, null, null, "angular", null, null);
        var server = new DeploymentPlanPart("server", "railway", "src/api", "src/api", null, null, null, null, "dotnet", "src/api/Dockerfile", null);
        var files = BuildCompleteFiles(website, server);
        files.Remove($"{website.RootDirectory}/angular.json");

        var issues = SplitOriginReadinessEvaluator.Evaluate(website, server, files);

        Assert.Contains(issues, issue => issue.Path.EndsWith("angular.json") && issue.Severity == DeploymentFileSeverity.Blocking);
        Assert.False(SplitOriginReadinessEvaluator.IsReady(issues));
    }

    [Fact]
    public void Evaluate_AllowsRecommendedIssues_WhenBlockingFilesPresent()
    {
        var website = new DeploymentPlanPart("website", "vercel", "client", null, null, null, null, null, "angular", null, null);
        var server = new DeploymentPlanPart("server", "railway", "src/api", "src/api", null, null, null, null, "dotnet", "src/api/Dockerfile", null);
        var files = BuildCompleteFiles(website, server);
        files.Remove("docs/DEPLOYMENT.md");

        var issues = SplitOriginReadinessEvaluator.Evaluate(website, server, files);

        Assert.Contains(issues, issue => issue.Severity == DeploymentFileSeverity.Recommended);
        Assert.True(SplitOriginReadinessEvaluator.IsReady(issues));
    }

    [Fact]
    public void Evaluate_FlagsProxyAntiPattern_AsBlocking()
    {
        var website = new DeploymentPlanPart("website", "vercel", "client", null, null, null, null, null, "angular", null, null);
        var server = new DeploymentPlanPart("server", "railway", "src/api", "src/api", null, null, null, null, "dotnet", "src/api/Dockerfile", null);
        var files = BuildCompleteFiles(website, server);
        files["client/vercel.json"] = """
            {
              "rewrites": [
                { "source": "/api/:path*", "destination": "https://api.example.com/api/:path*" }
              ]
            }
            """;

        var issues = SplitOriginReadinessEvaluator.Evaluate(website, server, files);

        Assert.Contains(issues, issue => issue.Path == "client/vercel.json" && issue.Severity == DeploymentFileSeverity.Blocking);
    }

    [Fact]
    public void Evaluate_FlagsAllowAllCors_AsRecommended()
    {
        var website = new DeploymentPlanPart("website", "vercel", "client", null, null, null, null, null, "angular", null, null);
        var server = new DeploymentPlanPart("server", "railway", "src/api", "src/api", null, null, null, null, "dotnet", "src/api/Dockerfile", null);
        var files = BuildCompleteFiles(website, server);
        files["src/api/Program.cs"] = """
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAll", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
            });
            """;

        var issues = SplitOriginReadinessEvaluator.Evaluate(website, server, files);

        Assert.Contains(issues, issue =>
            issue.Path == "src/api/Program.cs" &&
            issue.Severity == DeploymentFileSeverity.Recommended &&
            issue.Reason.Contains("AllowAll", StringComparison.OrdinalIgnoreCase));
        Assert.True(SplitOriginReadinessEvaluator.IsReady(issues));
    }

    [Fact]
    public void Evaluate_FlagsMissingEnvironmentFiles_WhenWriteApiEnvPresent()
    {
        var website = new DeploymentPlanPart("website", "vercel", "client", null, null, null, null, null, "angular", null, null);
        var server = new DeploymentPlanPart("server", "railway", "src/api", "src/api", null, null, null, null, "dotnet", "src/api/Dockerfile", null);
        var files = BuildCompleteFiles(website, server);
        files.Remove("client/src/environments/environment.production.ts");

        var issues = SplitOriginReadinessEvaluator.Evaluate(website, server, files);

        Assert.Contains(issues, issue =>
            issue.Path.Contains("environment.production.ts", StringComparison.OrdinalIgnoreCase) &&
            issue.Severity == DeploymentFileSeverity.Recommended);
    }

    private static Dictionary<string, string?> BuildCompleteFiles(DeploymentPlanPart website, DeploymentPlanPart server)
    {
        var clientPrefix = $"{website.RootDirectory}/";
        var serverRoot = server.ServiceDirectory ?? server.RootDirectory ?? string.Empty;
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["railway.toml"] = "[build]",
            [$"{clientPrefix}vercel.json"] = """{ "rewrites": [{ "source": "/(.*)", "destination": "/index.html" }], "buildCommand": "node scripts/write-api-env.mjs" }""",
            [$"{clientPrefix}scripts/write-api-env.mjs"] = "export {}",
            [$"{clientPrefix}src/app/core/interceptors/api-base.interceptor.ts"] = "export const apiBaseInterceptor = () => {};",
            [$"{clientPrefix}angular.json"] = """{ "projects": { "app": { "architect": { "build": { "configurations": { "production": { "fileReplacements": [{ "with": "environment.production.ts" }] } } } } } } }""",
            [$"{clientPrefix}src/environments/environment.ts"] = "export const environment = { production: false, apiBaseUrl: '' };",
            [$"{clientPrefix}src/environments/environment.production.ts"] = "export const environment = { production: true, apiBaseUrl: '' };",
            [$"{clientPrefix}src/app/app.config.ts"] = "provideHttpClient(withInterceptors([apiBaseInterceptor]))",
            [$"{serverRoot}/Controllers/HealthController.cs"] = "[Route(\"api/v1/health\")] public class HealthController {}",
            [$"{serverRoot}/Program.cs"] = "AllowedOrigins SetIsOriginAllowed vercel.app",
            [$"{serverRoot}/Controllers/AuthController.cs"] = "SameSiteMode.None",
            ["docs/DEPLOYMENT.md"] = "# deploy"
        };
    }
}
