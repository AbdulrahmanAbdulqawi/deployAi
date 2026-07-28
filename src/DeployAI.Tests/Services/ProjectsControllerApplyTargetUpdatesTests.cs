using System.Reflection;
using DeployAI.Api.Controllers;
using DeployAI.Data.Entities;

namespace DeployAI.Tests.Services;

public class ProjectsControllerApplyTargetUpdatesTests
{
    [Fact]
    public void ApplyTargetUpdates_KeepsBothCoolifyTargets_ForFullStackApp()
    {
        var projectId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var project = new Project
        {
            Id = projectId,
            UserId = Guid.NewGuid(),
            Name = "Demo",
            GitHubRepoFullName = "tester/demo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    CredentialId = credentialId,
                    ProviderProjectId = "website-uuid",
                    ConfigJson = """{"role":"website","rootDirectory":"client"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    CredentialId = credentialId,
                    ProviderProjectId = "server-uuid",
                    ConfigJson = """{"role":"server","serviceDirectory":"Api"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        };

        var targets = new List<ProjectsController.ProjectTargetRequest>
        {
            new("coolify", credentialId, "website-uuid", """{"role":"website","rootDirectory":"client-renamed"}"""),
            new("coolify", credentialId, "server-uuid", """{"role":"server","serviceDirectory":"Api"}""")
        };

        // ApplyTargetUpdates is private static; this exercises the exact bug scenario
        // (two targets sharing providerName "coolify") without standing up the full
        // controller's dependency graph.
        var method = typeof(ProjectsController).GetMethod(
            "ApplyTargetUpdates",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = Record.Exception(() => method!.Invoke(null, [project, targets]));

        Assert.Null(exception);
        Assert.Equal(2, project.DeployTargets.Count);
        Assert.Contains(project.DeployTargets, t => t.ProviderProjectId == "website-uuid" && t.ConfigJson.Contains("client-renamed"));
        Assert.Contains(project.DeployTargets, t => t.ProviderProjectId == "server-uuid");
    }

    [Fact]
    public void ApplyTargetUpdates_RemovesDroppedProviderRole_KeepsOthers()
    {
        var projectId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var project = new Project
        {
            Id = projectId,
            UserId = Guid.NewGuid(),
            Name = "Demo",
            GitHubRepoFullName = "tester/demo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    CredentialId = credentialId,
                    ProviderProjectId = "website-uuid",
                    ConfigJson = """{"role":"website"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    CredentialId = credentialId,
                    ProviderProjectId = "server-uuid",
                    ConfigJson = """{"role":"server"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        };

        var targets = new List<ProjectsController.ProjectTargetRequest>
        {
            new("coolify", credentialId, "website-uuid", """{"role":"website"}""")
        };

        var method = typeof(ProjectsController).GetMethod(
            "ApplyTargetUpdates",
            BindingFlags.NonPublic | BindingFlags.Static);

        method!.Invoke(null, [project, targets]);

        var remaining = Assert.Single(project.DeployTargets);
        Assert.Equal("website-uuid", remaining.ProviderProjectId);
    }
}
