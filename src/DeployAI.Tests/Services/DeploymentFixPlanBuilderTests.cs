using DeployAI.Api.Services;
using DeployAI.Api.Services.DeploymentTemplates;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data.Entities;

namespace DeployAI.Tests.Services;

public class DeploymentFixPlanBuilderTests
{
    [Fact]
    public void BuildSyntheticSplitOriginParts_ReturnsVercelRailway_ForVercelAngularWebsite()
    {
        var parts = DeploymentFixPlanBuilder.BuildSyntheticSplitOriginParts(
            ProviderNameValues.Vercel,
            "angular",
            new DeployTargetConfig
            {
                Role = "website",
                RootDirectory = "client",
                Framework = "angular"
            });

        Assert.Equal(2, parts.Count);
        Assert.Equal(ProviderNameValues.Vercel, parts[0].ProviderName);
        Assert.Equal(ProviderNameValues.Railway, parts[1].ProviderName);
    }

    [Fact]
    public void BuildSyntheticSplitOriginParts_ReturnsCoolifyFullStack_ForCoolifyAngularWebsite()
    {
        var parts = DeploymentFixPlanBuilder.BuildSyntheticSplitOriginParts(
            ProviderNameValues.Coolify,
            "angular",
            new DeployTargetConfig
            {
                Role = "website",
                RootDirectory = "client",
                Framework = "angular",
                OutputDirectory = "dist/app/browser"
            });

        Assert.Equal(2, parts.Count);
        Assert.All(parts, part => Assert.Equal(ProviderNameValues.Coolify, part.ProviderName));
        Assert.Equal("client", parts[0].RootDirectory);
        Assert.Equal("dotnet", parts[1].Framework);
    }

    [Fact]
    public void BuildSyntheticSplitOriginParts_ReturnsCoolifyFullStack_ForCoolifyDotnetServer()
    {
        var parts = DeploymentFixPlanBuilder.BuildSyntheticSplitOriginParts(
            ProviderNameValues.Coolify,
            "aspnetcore",
            new DeployTargetConfig
            {
                Role = "server",
                ServiceDirectory = "src/Api",
                Framework = "aspnetcore",
                DockerfilePath = "src/Api/Dockerfile"
            });

        Assert.Equal(2, parts.Count);
        Assert.Equal("website", parts[0].Role);
        Assert.Equal("client", parts[0].RootDirectory);
        Assert.Equal("server", parts[1].Role);
        Assert.Equal("src/Api", parts[1].ServiceDirectory);
    }

    [Fact]
    public void BuildSyntheticSplitOriginParts_ReturnsEmpty_ForCoolifyUnsupportedFramework()
    {
        var parts = DeploymentFixPlanBuilder.BuildSyntheticSplitOriginParts(
            ProviderNameValues.Coolify,
            "nextjs",
            new DeployTargetConfig { Role = "website", Framework = "nextjs" });

        Assert.Empty(parts);
    }

    // An Angular + .NET pair on one Coolify server is a compose deployment, so a failing build
    // must be handed compose references — not split-origin scaffolding it never had.
    [Fact]
    public void ResolveForSplitOriginFix_IncludesComposeTemplates_WhenCoolifyWebsiteBuildFails()
    {
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var parts = DeploymentFixPlanBuilder.BuildSyntheticSplitOriginParts(
            ProviderNameValues.Coolify,
            "angular",
            new DeployTargetConfig
            {
                Role = "website",
                RootDirectory = "client",
                Framework = "angular"
            });

        var references = resolver.ResolveForSplitOriginFix(
            parts,
            ["client/Dockerfile", "client/nginx.conf"]);

        Assert.Contains(references, template => template.TargetPath == "client/Dockerfile");
        Assert.Contains(references, template => template.TargetPath == "client/nginx.conf");
        Assert.DoesNotContain(references, template => template.TemplateId.Contains("write-api-env"));
        Assert.DoesNotContain(references, template => template.TemplateId.Contains("vercel-json"));
        Assert.DoesNotContain(references, template => template.TemplateId.Contains("railway-toml"));
    }
}

public class DeploymentTargetResolutionTests
{
    [Fact]
    public void FindWebsiteAndServerTargets_UsesRole_ForCoolifyFullStack()
    {
        var websiteDeployTarget = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProviderName = ProviderNameValues.Coolify,
            ConfigJson = """{"role":"website","framework":"angular"}"""
        };
        var serverDeployTarget = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProviderName = ProviderNameValues.Coolify,
            ConfigJson = """{"role":"server","framework":"dotnet"}"""
        };

        var targets = new[]
        {
            new DeploymentTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Coolify,
                DeployTarget = websiteDeployTarget
            },
            new DeploymentTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Coolify,
                DeployTarget = serverDeployTarget
            }
        };

        var website = DeploymentTargetResolution.FindWebsiteTarget(targets);
        var server = DeploymentTargetResolution.FindServerTarget(targets);

        Assert.NotNull(website);
        Assert.NotNull(server);
        Assert.True(DeploymentTargetResolution.IsCoolifyFullStack(targets));
    }

    [Fact]
    public void ResolveProviderPairs_ReturnsBothPairs_ForMixedVercelRailwayAndCoolifyProject()
    {
        var vercelWebsiteDeployTarget = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProviderName = ProviderNameValues.Vercel,
            ConfigJson = """{"role":"website","framework":"angular"}"""
        };
        var railwayServerDeployTarget = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProviderName = ProviderNameValues.Railway,
            ConfigJson = """{"role":"server","framework":"dotnet"}"""
        };
        var coolifyWebsiteDeployTarget = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProviderName = ProviderNameValues.Coolify,
            ConfigJson = """{"role":"website","framework":"angular"}"""
        };
        var coolifyServerDeployTarget = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProviderName = ProviderNameValues.Coolify,
            ConfigJson = """{"role":"server","framework":"dotnet"}"""
        };

        var targets = new[]
        {
            new DeploymentTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Vercel,
                DeployTarget = vercelWebsiteDeployTarget
            },
            new DeploymentTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Railway,
                DeployTarget = railwayServerDeployTarget
            },
            new DeploymentTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Coolify,
                DeployTarget = coolifyWebsiteDeployTarget
            },
            new DeploymentTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Coolify,
                DeployTarget = coolifyServerDeployTarget
            }
        };

        var pairs = DeploymentTargetResolution.ResolveProviderPairs(targets);

        Assert.Equal(2, pairs.Count);
        Assert.Contains(pairs, pair =>
            pair.Website.ProviderName == ProviderNameValues.Vercel &&
            pair.Server.ProviderName == ProviderNameValues.Railway);
        Assert.Contains(pairs, pair =>
            pair.Website.ProviderName == ProviderNameValues.Coolify &&
            pair.Server.ProviderName == ProviderNameValues.Coolify);
    }

    [Fact]
    public void ResolveProviderPairs_DeployTargetOverload_ReturnsBothPairs_ForMixedProject()
    {
        var targets = new[]
        {
            new DeployTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Vercel,
                ConfigJson = """{"role":"website","framework":"angular"}"""
            },
            new DeployTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Railway,
                ConfigJson = """{"role":"server","framework":"dotnet"}"""
            },
            new DeployTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Coolify,
                ConfigJson = """{"role":"website","framework":"angular"}"""
            },
            new DeployTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Coolify,
                ConfigJson = """{"role":"server","framework":"dotnet"}"""
            }
        };

        var pairs = DeploymentTargetResolution.ResolveProviderPairs(targets);

        Assert.Equal(2, pairs.Count);
        Assert.Contains(pairs, pair =>
            pair.Website.ProviderName == ProviderNameValues.Vercel &&
            pair.Server.ProviderName == ProviderNameValues.Railway);
        Assert.Contains(pairs, pair =>
            pair.Website.ProviderName == ProviderNameValues.Coolify &&
            pair.Server.ProviderName == ProviderNameValues.Coolify);
    }
}
