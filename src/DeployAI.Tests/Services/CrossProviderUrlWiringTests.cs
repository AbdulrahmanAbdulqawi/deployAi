using DeployAI.Api.Services;
using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

public class CrossProviderUrlWiringTests
{
    [Theory]
    [InlineData("angular", true)]
    [InlineData("vite", false)]
    [InlineData("react", false)]
    [InlineData(null, true)]
    public void UsesRelativeApiPaths_MatchesFramework(string? framework, bool expected)
    {
        Assert.Equal(expected, CrossProviderUrlWiring.UsesRelativeApiPaths(framework));
    }

    [Fact]
    public void ResolveApiEnvKeys_IncludesAngularSplitOriginKeys()
    {
        var keys = CrossProviderUrlWiring.ResolveApiEnvKeys("angular");
        Assert.Contains("DEPLOYAI_API_URL", keys);
        Assert.Contains("API_BASE_URL", keys);
        Assert.Contains("NG_APP_API_URL", keys);
        Assert.Contains("API_URL", keys);
        Assert.DoesNotContain("IDAARA_API_URL", keys);
    }

    [Fact]
    public void ShouldUseSplitOrigin_ForAngularDockerStack()
    {
        Assert.True(CrossProviderUrlWiring.ShouldUseSplitOrigin("angular", "docker"));
        Assert.False(CrossProviderUrlWiring.ShouldUseSplitOrigin("vite", "docker"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("blazor")]
    [InlineData("remix")]
    public void ShouldUseSplitOrigin_ReturnsFalse_ForUnrecognizedFrontendWithDockerBackend(string? websiteFramework)
    {
        // An unrecognized frontend framework falls through UsesRelativeApiPaths' default
        // (true), and "docker" is a generic Railway backend framework that isn't necessarily
        // .NET. Without an explicit Angular signal, this combination must not be classified
        // as the Angular+.NET split-origin scenario — doing so would trigger irrelevant
        // Angular/.NET Blocking readiness findings for an unrelated stack.
        Assert.False(CrossProviderUrlWiring.ShouldUseSplitOrigin(websiteFramework, "docker"));
    }

    [Fact]
    public void BuildServerRuntimeEnvAssignments_UsesIndexedAllowedOrigins_ForSplitOrigin()
    {
        var assignments = CrossProviderUrlWiring.BuildServerRuntimeEnvAssignments(
            "docker",
            "angular",
            "https://idaara-kappa.vercel.app",
            ["https://idaara-kappa.vercel.app"],
            "https://idaara-api-production.up.railway.app");

        Assert.Contains(assignments, a => a.Key == "AllowedOrigins__0" && a.Value == "https://idaara-kappa.vercel.app");
        Assert.Contains(assignments, a => a.Key == "App__BaseUrl" && a.Value == "https://idaara-kappa.vercel.app");
        Assert.DoesNotContain(assignments, a => a.Key == "App__ApiUrl");
        Assert.DoesNotContain(assignments, a => a.Key == "CORS_ALLOWED_ORIGINS");
    }

    [Fact]
    public void BuildServerRuntimeEnvAssignments_UsesRailwayApi_ForViteClient()
    {
        var assignments = CrossProviderUrlWiring.BuildServerRuntimeEnvAssignments(
            "dotnet",
            "vite",
            "https://app.vercel.app",
            ["https://app.vercel.app"],
            "https://api-production.up.railway.app");

        Assert.Contains(assignments, a => a.Key == "App__ApiUrl" && a.Value == "https://api-production.up.railway.app");
    }

    [Fact]
    public void ValidateIgnoredRailwayEnvKeys_WarnsOnCorsAllowedOrigins()
    {
        var warnings = CrossProviderUrlWiring.ValidateIgnoredRailwayEnvKeys(
        [
            new CrossProviderUrlWiring.ProviderEnvVarSnapshot("CORS_ALLOWED_ORIGINS", "https://app.vercel.app")
        ]);

        Assert.Single(warnings);
    }
}

public class SplitOriginDetectionTests
{
    [Fact]
    public void PlanUsesSplitOrigin_WhenAngularVercelAndRailwayServer()
    {
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "vercel", "idaara.client", null, null, null, null, null, "angular", null, null),
            new("server", "railway", ".", "iDaara.Server", null, null, null, null, "docker", "iDaara.Server/Dockerfile", null)
        };

        Assert.True(SplitOriginDetection.PlanUsesSplitOrigin(parts));
    }

    [Fact]
    public void PlanUsesSplitOrigin_WhenAngularCoolifyFullStack()
    {
        var parts = new List<DeploymentPlanPart>
        {
            new("website", "coolify", "client", null, null, null, null, null, "angular", null, null),
            new("server", "coolify", "src/api", "src/api", null, null, null, null, "dotnet", null, null)
        };

        Assert.True(SplitOriginDetection.PlanUsesSplitOrigin(parts));
    }

    [Fact]
    public void BuildReadinessFilePaths_OmitsVercelAndRailwayFiles_ForCoolifyFullStack()
    {
        var website = new DeploymentPlanPart("website", "coolify", "client", null, null, null, null, null, "angular", null, null);
        var server = new DeploymentPlanPart("server", "coolify", "src/api", "src/api", null, null, null, null, "dotnet", null, null);

        var paths = SplitOriginDetection.BuildReadinessFilePaths(website, server);

        Assert.DoesNotContain(paths, path => path.Equals("railway.toml", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, path => path.Equals("client/vercel.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, path => path.Contains("api-base.interceptor.ts", StringComparison.OrdinalIgnoreCase));
    }
}
