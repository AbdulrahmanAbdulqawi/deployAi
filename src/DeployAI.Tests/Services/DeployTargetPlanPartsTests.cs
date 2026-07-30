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
