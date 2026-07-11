using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class ProjectBranchDeployServiceTests
{
    [Fact]
    public async Task UseBranchAndDeployAsync_SetsDefaultBranch_WithoutDeploy()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GitHubTokenEncrypted = [1]
        });
        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "App",
            GitHubRepoFullName = "owner/repo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var gitHub = new Mock<IGitHubService>();
        gitHub
            .Setup(service => service.GetBranchHeadShaAsync("token", "owner", "repo", "deployai/fix-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha123");

        var orchestrator = new Mock<IDeploymentOrchestrator>();
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        var service = new ProjectBranchDeployService(
            db,
            gitHub.Object,
            orchestrator.Object,
            encryption.Object);

        var result = await service.UseBranchAndDeployAsync(
            userId,
            projectId,
            "deployai/fix-1",
            triggerFullDeploy: false,
            CancellationToken.None);

        Assert.Equal("deployai/fix-1", result.Branch);
        Assert.Null(result.DeploymentId);

        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        Assert.Equal("deployai/fix-1", project.DefaultBranch);
        orchestrator.Verify(
            o => o.TriggerAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<DeploymentTriggeredBy>()),
            Times.Never);
    }

    [Fact]
    public async Task UseBranchAndDeployAsync_TriggersDeployment_WhenRequested()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GitHubTokenEncrypted = [1]
        });
        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "App",
            GitHubRepoFullName = "owner/repo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var gitHub = new Mock<IGitHubService>();
        gitHub
            .Setup(service => service.GetBranchHeadShaAsync("token", "owner", "repo", "deployai/setup-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha123");

        var orchestrator = new Mock<IDeploymentOrchestrator>();
        orchestrator
            .Setup(o => o.TriggerAsync(projectId, userId, "deployai/setup-1", It.IsAny<CancellationToken>(), DeploymentTriggeredBy.User))
            .ReturnsAsync(new TriggerDeploymentResult(deploymentId, "pending", []));

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        var service = new ProjectBranchDeployService(
            db,
            gitHub.Object,
            orchestrator.Object,
            encryption.Object);

        var result = await service.UseBranchAndDeployAsync(
            userId,
            projectId,
            "deployai/setup-1",
            triggerFullDeploy: true,
            CancellationToken.None);

        Assert.Equal(deploymentId, result.DeploymentId);
        orchestrator.Verify(
            o => o.TriggerAsync(projectId, userId, "deployai/setup-1", It.IsAny<CancellationToken>(), DeploymentTriggeredBy.User),
            Times.Once);
    }

    [Fact]
    public async Task UseBranchAndDeployAsync_RejectsMissingBranch()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GitHubTokenEncrypted = [1]
        });
        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "App",
            GitHubRepoFullName = "owner/repo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var gitHub = new Mock<IGitHubService>();
        gitHub
            .Setup(service => service.GetBranchHeadShaAsync("token", "owner", "repo", "missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        var service = new ProjectBranchDeployService(
            db,
            gitHub.Object,
            new Mock<IDeploymentOrchestrator>().Object,
            encryption.Object);

        await Assert.ThrowsAsync<DeployAIException>(() =>
            service.UseBranchAndDeployAsync(userId, projectId, "missing", false, CancellationToken.None));
    }

    private static DeployAIDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeployAIDbContext(options);
    }
}
