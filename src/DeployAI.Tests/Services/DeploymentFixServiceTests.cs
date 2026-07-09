using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class DeploymentFixServiceTests
{
    [Fact]
    public async Task GenerateFixPullRequestAsync_UsesDeploymentBranchAsPrBase()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var deploymentTargetId = Guid.NewGuid();
        var deployTargetId = Guid.NewGuid();

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
            Name = "Demo",
            GitHubRepoFullName = "owner/repo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            "Build failed",
            "error TS2304",
            ["src/app/app.component.ts"],
            true);

        db.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            ProjectId = projectId,
            Branch = "feature/deploy",
            Status = DeploymentStatuses.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
            GitCommitSha = "abc123"
        });

        var deployTarget = new DeployTarget
        {
            Id = deployTargetId,
            ProjectId = projectId,
            ProviderName = "vercel",
            CredentialId = Guid.NewGuid(),
            ProviderProjectId = "demo",
            ConfigJson = """{"role":"website","framework":"angular"}""",
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.DeployTargets.Add(deployTarget);

        db.DeploymentTargets.Add(new DeploymentTarget
        {
            Id = deploymentTargetId,
            DeploymentId = deploymentId,
            DeployTargetId = deployTargetId,
            ProviderName = "vercel",
            Status = DeploymentStatuses.Failed,
            FailureAnalysisJson = DeploymentFailureAnalysisJson.ToJson(analysis),
            DeployTarget = deployTarget
        });

        await db.SaveChangesAsync();

        var generator = new Mock<IDeploymentFixGenerator>();
        generator
            .Setup(g => g.GenerateFixFilesAsync(
                It.IsAny<Guid>(),
                "owner",
                "repo",
                "abc123",
                "token",
                "vercel",
                "angular",
                It.IsAny<DeployTargetConfig>(),
                It.IsAny<DeploymentFailureAnalysis>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<CancellationToken>(),
                null))
            .ReturnsAsync([new GeneratedDeploymentFile("src/app/app.component.ts", "fixed")]);

        var gitHub = new Mock<IGitHubService>();
        gitHub
            .Setup(s => s.GetBranchHeadShaAsync("token", "owner", "repo", "feature/deploy", It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        gitHub
            .Setup(s => s.CreateBranchAsync("token", "owner", "repo", It.IsAny<string>(), "abc123", It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        gitHub
            .Setup(s => s.CommitFilesAsync("token", "owner", "repo", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("commit456");
        gitHub
            .Setup(s => s.CreatePullRequestAsync(
                "token",
                "owner",
                "repo",
                It.IsAny<string>(),
                It.IsAny<string>(),
                "feature/deploy",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResponse(7, "https://github.com/owner/repo/pull/7"));

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        var failureAnalyzer = new Mock<IDeploymentFailureAnalyzer>();
        var verification = new Mock<IDeploymentVerificationService>();

        var service = new DeploymentFixService(db, gitHub.Object, generator.Object, failureAnalyzer.Object, verification.Object, encryption.Object);
        var result = await service.GenerateFixPullRequestAsync(userId, deploymentId, deploymentTargetId, null, CancellationToken.None);

        Assert.Equal(7, result.PullRequestNumber);
        gitHub.Verify(s => s.CreatePullRequestAsync(
            "token",
            "owner",
            "repo",
            It.IsAny<string>(),
            It.IsAny<string>(),
            "feature/deploy",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static DeployAIDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeployAIDbContext(options);
    }
}
