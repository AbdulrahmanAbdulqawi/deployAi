using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Data.Entities;

namespace DeployAI.Tests.Services;

/// <summary>
/// A compose deployment is two halves in one application. These cover the moment that fact was
/// lost: reading the project back from its targets.
/// </summary>
public class DeployTargetPlanPartsTests
{
    [Fact]
    public void AComposeTargetStandsForBothHalvesOfThePlan()
    {
        // Mirqab's plan named a site, an API and a database, and the wizard was right to create one
        // Coolify application for them. But one application meant one target, the target carried the
        // website role, and from then on the project read as a lone Angular site.
        var parts = DeployTargetPlanParts.FromTargets([ComposeTarget()]);

        Assert.NotNull(SplitOriginDetection.FindWebsitePart(parts));
        var server = SplitOriginDetection.FindServerPart(parts);
        Assert.NotNull(server);
        Assert.Equal("src/Mirqab.Api", server.ServiceDirectory);
        Assert.Equal("dotnet", server.Framework);
    }

    [Fact]
    public void AComposeProjectIsRecognisedAsACompose()
    {
        // The assertion that decides whether the compose readiness rules run at all. With one part
        // this answered false, so a repository with no docker-compose.coolify.yml was reported ready
        // and the deploy went ahead to build the client alone on Nixpacks.
        var parts = DeployTargetPlanParts.FromTargets([ComposeTarget()]);

        Assert.True(SplitOriginDetection.PlanUsesSingleOriginCompose(parts));
    }

    [Fact]
    public void TheComposeFileIsRequiredOfAComposeProject()
    {
        // End of the chain: the missing file is Blocking, which is what stops the deploy. Before
        // this, the repository below was indistinguishable from a ready one.
        var parts = DeployTargetPlanParts.FromTargets([ComposeTarget()]);
        var website = SplitOriginDetection.FindWebsitePart(parts)!;
        var server = SplitOriginDetection.FindServerPart(parts)!;

        var issues = SplitOriginDetection.EvaluateRepositoryFiles(
            usesSplitOrigin: false,
            website,
            server,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains(issues, issue =>
            issue.Path == "docker-compose.coolify.yml" &&
            issue.Severity == DeploymentFileSeverity.Blocking);
    }

    [Fact]
    public void AComposeServerBuiltFromTheRepositoryRoot_KeepsItsBuildRootSeparateFromItsSource()
    {
        // Mirqab's actual shape, not the simplified fixture above: the api's Dockerfile builds from
        // the repository root (a root-context multi-stage build), while its own source — Program.cs,
        // Controllers, appsettings.json — is nested at src/Mirqab.Api. One stored value used to
        // answer both questions; readiness asked "where is the Dockerfile" using the value that now
        // (correctly) answers "where is the source", and reported src/Mirqab.Api/Dockerfile missing
        // for an app whose Dockerfile has always been at the repository root.
        var target = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProviderName = "coolify",
            ConfigJson = """
                {"role":"website","framework":"angular","rootDirectory":"client",
                 "composeFileLocation":"docker-compose.coolify.yml","domainServiceName":"web",
                 "composeServerDirectory":"src/Mirqab.Api","composeServerRootDirectory":"",
                 "composeServerFramework":"docker"}
                """
        };

        var server = SplitOriginDetection.FindServerPart(DeployTargetPlanParts.FromTargets([target]))!;

        Assert.Equal(string.Empty, server.RootDirectory);
        Assert.Equal("src/Mirqab.Api", server.ServiceDirectory);
    }

    [Fact]
    public void AnOrdinaryTargetStillStandsForOnePart()
    {
        // The expansion must be confined to compose targets. A normal Coolify website is one part,
        // and inventing a server beside it would make every single-app project look full-stack.
        var parts = DeployTargetPlanParts.FromTargets([
            new DeployTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = "coolify",
                ConfigJson = """{"role":"website","framework":"angular","rootDirectory":"client"}"""
            }
        ]);

        Assert.Single(parts);
        Assert.Null(SplitOriginDetection.FindServerPart(parts));
    }

    private static DeployTarget ComposeTarget() => new()
    {
        Id = Guid.NewGuid(),
        ProviderName = "coolify",
        ConfigJson = """
            {"role":"website","framework":"angular","rootDirectory":"client",
             "composeFileLocation":"docker-compose.coolify.yml","domainServiceName":"web",
             "composeServerDirectory":"src/Mirqab.Api","composeServerFramework":"dotnet"}
            """
    };
}
