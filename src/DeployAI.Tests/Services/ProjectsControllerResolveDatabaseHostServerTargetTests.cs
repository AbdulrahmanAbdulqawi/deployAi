using System.Reflection;
using DeployAI.Api.Controllers;
using DeployAI.Data.Entities;

namespace DeployAI.Tests.Services;

/// <summary>
/// "Add" for Mirqab's Postgres answered "Connect a server (Railway or Coolify) before adding
/// databases" against a project that has exactly one Coolify application. The filter required
/// role == "server", which only a split deploy's dedicated server target carries — a single-origin
/// compose target stands for both halves and keeps the website's role, because that is the half
/// facing the browser. Reflection, the same way ApplyTargetUpdates is exercised here, because the
/// resolver is private static and the fact worth asserting is its return value, not a wired-up
/// controller.
/// </summary>
public class ProjectsControllerResolveDatabaseHostServerTargetTests
{
    [Fact]
    public void ResolvesTheComposeTarget_WhenItIsTheOnlyOneAndCarriesTheWebsiteRole()
    {
        var project = new Project { Id = Guid.NewGuid() };
        var composeTarget = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ProviderName = "coolify",
            ConfigJson = """
                {"role":"website","rootDirectory":"client",
                 "composeFileLocation":"docker-compose.coolify.yml",
                 "composeServerDirectory":"src/Mirqab.Api"}
                """
        };
        project.DeployTargets.Add(composeTarget);

        var resolved = Resolve(project);

        Assert.Equal(composeTarget.Id, resolved?.Id);
    }

    [Fact]
    public void StillResolvesAnOrdinaryServerTarget_ForASplitDeploy()
    {
        var project = new Project { Id = Guid.NewGuid() };
        var serverTarget = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ProviderName = "railway",
            ConfigJson = """{"role":"server","rootDirectory":"api"}"""
        };
        project.DeployTargets.Add(serverTarget);

        var resolved = Resolve(project);

        Assert.Equal(serverTarget.Id, resolved?.Id);
    }

    [Fact]
    public void ResolvesNothing_ForAPlainWebsiteThatIsNotCompose()
    {
        // The guard the widened rule must not lose: an ordinary website-only project has nowhere
        // to attach a database either, and must keep answering "connect a server" rather than
        // silently attaching a database to a frontend.
        var project = new Project { Id = Guid.NewGuid() };
        var websiteTarget = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ProviderName = "vercel",
            ConfigJson = """{"role":"website","rootDirectory":"client"}"""
        };
        project.DeployTargets.Add(websiteTarget);

        Assert.Null(Resolve(project));
    }

    private static DeployTarget? Resolve(Project project)
    {
        var method = typeof(ProjectsController).GetMethod(
            "ResolveDatabaseHostServerTarget",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (DeployTarget?)method.Invoke(null, [project]);
    }
}
