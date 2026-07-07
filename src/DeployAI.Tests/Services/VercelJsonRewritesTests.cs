using DeployAI.Api.Services;
using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

public class VercelJsonRewritesTests
{
    [Fact]
    public void TryBuildUpdatedContent_InsertsProxyRewritesBeforeSpaFallback()
    {
        const string existing = """
        {
          "buildCommand": "npm run build",
          "outputDirectory": "dist/client/browser",
          "rewrites": [
            { "source": "/(.*)", "destination": "/index.html" }
          ]
        }
        """;

        var updated = VercelJsonRewrites.TryBuildUpdatedContent(
            existing,
            "https://idaara-api-production.up.railway.app",
            out var content);
        Assert.True(updated);
        Assert.Contains("\"/api/:path*\"", content);
        Assert.Contains("https://idaara-api-production.up.railway.app/api/:path*", content);
        Assert.Contains("\"/hubs/:path*\"", content);
        Assert.True(content.IndexOf("/api/:path*", StringComparison.Ordinal) <
                    content.IndexOf("/(.*)", StringComparison.Ordinal));
    }

    [Fact]
    public void TryBuildUpdatedContent_SkipsWhenRewritesAlreadyPresent()
    {
        const string existing = """
        {
          "rewrites": [
            { "source": "/api/:path*", "destination": "https://api.example.com/api/:path*" },
            { "source": "/hubs/:path*", "destination": "https://api.example.com/hubs/:path*" },
            { "source": "/(.*)", "destination": "/index.html" }
          ]
        }
        """;

        Assert.False(VercelJsonRewrites.TryBuildUpdatedContent(
            existing,
            "https://api.example.com",
            out var content));
        Assert.Equal(existing, content);
    }

    [Fact]
    public void EnrichWithBuildSettings_AddsAngularDefaultsForWebsiteRoot()
    {
        var config = DeployTargetConfig.FromProfile(
            new FrontendBuildProfile("idaara.client", "npm run build", "npm install", "dist/idaara.client", "angular"),
            "website");

        var enriched = VercelJsonRewrites.EnrichWithBuildSettings(
            """{"rewrites":[{"source":"/api/:path*","destination":"https://api.example.com/api/:path*"}]}""",
            config);

        Assert.Contains("\"buildCommand\": \"npm run build\"", enriched);
        Assert.Contains("\"outputDirectory\": \"dist/idaara.client\"", enriched);
    }

    [Fact]
    public void TryBuildUpdatedContent_ReordersProxyRewritesBeforeSpaFallback()
    {
        const string existing = """
        {
          "rewrites": [
            { "source": "/(.*)", "destination": "/index.html" },
            { "source": "/api/:path*", "destination": "https://api.example.com/api/:path*" },
            { "source": "/hubs/:path*", "destination": "https://api.example.com/hubs/:path*" }
          ]
        }
        """;

        var updated = VercelJsonRewrites.TryBuildUpdatedContent(
            existing,
            "https://api.example.com",
            out var content);
        Assert.True(updated);
        Assert.True(content.IndexOf("/api/:path*", StringComparison.Ordinal) <
                    content.IndexOf("/(.*)", StringComparison.Ordinal));
    }

    [Fact]
    public void TryBuildUpdatedContent_RemovesLegacyRoutesThatOverrideRewrites()
    {
        const string existing = """
        {
          "routes": [
            { "src": "/(.*)", "dest": "/index.html" }
          ],
          "rewrites": [
            { "source": "/api/:path*", "destination": "https://api.example.com/api/:path*" },
            { "source": "/hubs/:path*", "destination": "https://api.example.com/hubs/:path*" },
            { "source": "/(.*)", "destination": "/index.html" }
          ]
        }
        """;

        var updated = VercelJsonRewrites.TryBuildUpdatedContent(
            existing,
            "https://api.example.com",
            out var content);
        Assert.True(updated);
        Assert.DoesNotContain("\"routes\"", content);
    }

    [Fact]
    public void ResolvePrimaryPath_UsesWebsiteRootDirectory()
    {
        var config = DeployTargetConfig.FromProfile(
            new FrontendBuildProfile("idaara.client", "npm run build", "npm install", "dist/idaara.client", "angular"),
            "website");

        Assert.Equal("idaara.client/vercel.json", VercelJsonRewrites.ResolvePrimaryPath(config));
    }

    [Fact]
    public void HasApiProxyAntiPattern_DetectsApiRewrite()
    {
        const string content = """
        {
          "rewrites": [
            { "source": "/api/:path*", "destination": "https://api.example.com/api/:path*" }
          ]
        }
        """;

        Assert.True(VercelJsonRewrites.HasApiProxyAntiPattern(content));
    }

    [Fact]
    public void TryBuildSpaOnlyContent_RemovesProxyRewrites()
    {
        const string existing = """
        {
          "rewrites": [
            { "source": "/api/:path*", "destination": "https://api.example.com/api/:path*" },
            { "source": "/(.*)", "destination": "/index.html" }
          ]
        }
        """;

        var config = DeployTargetConfig.FromProfile(
            new FrontendBuildProfile("idaara.client", "npm run build", "npm install", "dist/idaara.client", "angular"),
            "website");

        var updated = VercelJsonRewrites.TryBuildSpaOnlyContent(existing, config, out var content);
        Assert.True(updated);
        Assert.DoesNotContain("/api/:path*", content);
        Assert.Contains("/index.html", content);
        Assert.Contains("write-api-env.mjs", content);
    }

    [Fact]
    public void EnsureWriteApiEnvInBuildCommand_PrependsScriptWhenMissing()
    {
        var updated = VercelJsonRewrites.EnsureWriteApiEnvInBuildCommand("npm run build");

        Assert.Equal("node scripts/write-api-env.mjs && npm run build", updated);
    }

    [Fact]
    public void TryBuildSpaOnlyContent_AddsWriteApiEnvWhenOnlySpaRewritePresent()
    {
        const string existing = """
        {
          "buildCommand": "npm run build",
          "outputDirectory": "dist/tickethub.client/browser",
          "rewrites": [
            { "source": "/(.*)", "destination": "/index.html" }
          ]
        }
        """;

        var config = DeployTargetConfig.FromProfile(
            new FrontendBuildProfile("tickethub.client", "npm run build", "npm install", "dist/tickethub.client/browser", "angular"),
            "website");

        var updated = VercelJsonRewrites.TryBuildSpaOnlyContent(existing, config, out var content);
        Assert.True(updated);
        Assert.Contains("write-api-env.mjs", content);
    }

    [Fact]
    public void NeedsWriteApiEnvBuildCommand_ReturnsTrueWhenScriptMissing()
    {
        const string existing = """
        {
          "buildCommand": "npm run build"
        }
        """;

        var config = DeployTargetConfig.FromProfile(
            new FrontendBuildProfile("tickethub.client", "npm run build", null, "dist/tickethub.client/browser", "angular"),
            "website");

        Assert.True(VercelJsonRewrites.NeedsWriteApiEnvBuildCommand(existing, config));
    }
}
