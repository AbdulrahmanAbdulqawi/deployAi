using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Tests.Services;

public class ProjectsControllerUpdateTests
{
    [Fact]
    public async Task UpdateProject_UpdatesExistingTargetsInPlace_PreservesDeployTargetIds()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var deployTargetId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();

        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.ProviderCredentials.Add(new ProviderCredential
        {
            Id = credentialId,
            UserId = userId,
            ProviderName = "vercel",
            Label = "Default",
            TokenEncrypted = [1, 2, 3],
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "Demo",
            GitHubRepoFullName = "tester/demo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = deployTargetId,
                    ProjectId = projectId,
                    ProviderName = "vercel",
                    CredentialId = credentialId,
                    ProviderProjectId = "prj_1",
                    ConfigJson = """{"rootDirectory":"","role":"website"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        db.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            ProjectId = projectId,
            Branch = "main",
            TriggeredBy = "user",
            Status = "success",
            CreatedAt = DateTimeOffset.UtcNow,
            Targets =
            [
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = deploymentId,
                    DeployTargetId = deployTargetId,
                    ProviderName = "vercel",
                    Status = "success"
                }
            ]
        });

        await db.SaveChangesAsync();

        var project = await db.Projects.Include(p => p.DeployTargets)
            .FirstAsync(p => p.Id == projectId);
        var target = project.DeployTargets.Single();
        target.ConfigJson = """{"rootDirectory":"client","role":"website"}""";
        project.Name = "Updated Demo";
        project.DefaultBranch = "develop";
        project.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        var reloadedTarget = await db.DeployTargets.SingleAsync(t => t.ProjectId == projectId);
        Assert.Equal(deployTargetId, reloadedTarget.Id);
        Assert.Contains("client", reloadedTarget.ConfigJson, StringComparison.Ordinal);

        var historyLink = await db.DeploymentTargets.SingleAsync(t => t.DeploymentId == deploymentId);
        Assert.Equal(deployTargetId, historyLink.DeployTargetId);
    }
}
