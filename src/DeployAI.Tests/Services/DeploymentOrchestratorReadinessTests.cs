using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class DeploymentOrchestratorReadinessTests
{
    [Fact]
    public void NotReadyMessage_NamesTheFileAndTheSetupThatProducesIt()
    {
        // "Deployment files are missing or invalid. Generate the split-origin setup" was the whole
        // message for every shape. A compose app's setup is a different one, so the only instruction
        // offered was the wrong instruction — and no file was named, so what to do about it had to be
        // worked out against a repository the reader may not have open.
        var message = DeploymentOrchestrator.BuildNotReadyMessage(new DeploymentReadinessResult(
            IsReady: false,
            CommitSha: "abc1234",
            UsesSplitOrigin: false,
            MissingFiles:
            [
                new MissingDeploymentFile(
                    "docker-compose.coolify.yml",
                    "A Docker Compose file is required.",
                    DeploymentFileSeverity.Blocking),
                new MissingDeploymentFile(
                    "docs/DEPLOYMENT.md",
                    "Document the compose env vars.",
                    DeploymentFileSeverity.Recommended)
            ],
            Warnings: [],
            UsesSingleOriginCompose: true));

        Assert.Contains("docker-compose.coolify.yml", message);
        Assert.Contains("compose setup", message);
        // Only what blocks: listing the recommended file too would make an optional doc read as the
        // reason the deploy stopped.
        Assert.DoesNotContain("DEPLOYMENT.md", message);
        Assert.DoesNotContain("split-origin", message);
    }

    [Fact]
    public void NotReadyMessage_KeepsTheSplitOriginInstructionForASplitOriginApp()
    {
        var message = DeploymentOrchestrator.BuildNotReadyMessage(new DeploymentReadinessResult(
            IsReady: false,
            CommitSha: null,
            UsesSplitOrigin: true,
            MissingFiles: [],
            Warnings: []));

        Assert.Contains("split-origin", message);
    }

    [Fact]
    public async Task TriggerAsync_ThrowsWhenDeploymentNotReady()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

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
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    ProviderName = "vercel",
                    CredentialId = credentialId,
                    ProviderProjectId = "demo",
                    ConfigJson = """{"role":"website","framework":"angular"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        await db.SaveChangesAsync();

        var readiness = new DeploymentReadinessResult(
            false,
            "abc123",
            true,
            [new MissingDeploymentFile("client/vercel.json", "missing", DeploymentFileSeverity.Blocking)],
            []);

        var orchestrator = CreateOrchestrator(db, readiness);
        var exception = await Assert.ThrowsAsync<DeployAIException>(() =>
            orchestrator.TriggerAsync(projectId, userId, "main", CancellationToken.None));

        Assert.Equal("deployment_not_ready", exception.ErrorCode);
    }

    [Fact]
    public async Task TriggerTargetAsync_ThrowsWhenDeploymentNotReady()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var vercelTargetId = Guid.NewGuid();

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
                    Id = vercelTargetId,
                    ProjectId = projectId,
                    ProviderName = "vercel",
                    CredentialId = credentialId,
                    ProviderProjectId = "demo",
                    ConfigJson = """{"role":"website","framework":"angular"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        await db.SaveChangesAsync();

        var readiness = new DeploymentReadinessResult(false, "abc123", true, [], []);
        var orchestrator = CreateOrchestrator(db, readiness);
        var exception = await Assert.ThrowsAsync<DeployAIException>(() =>
            orchestrator.TriggerTargetAsync(projectId, userId, vercelTargetId, "main", CancellationToken.None));

        Assert.Equal("deployment_not_ready", exception.ErrorCode);
    }

    [Fact]
    public async Task TriggerTargetAsync_PersistsGitCommitSha()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var vercelTargetId = Guid.NewGuid();

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
                    Id = vercelTargetId,
                    ProjectId = projectId,
                    ProviderName = "vercel",
                    CredentialId = credentialId,
                    ProviderProjectId = "demo",
                    ConfigJson = """{"role":"website","framework":"angular"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        await db.SaveChangesAsync();

        var readiness = new DeploymentReadinessResult(true, "deadbeef", false, [], []);
        var gitHub = new Mock<IGitHubService>();
        gitHub
            .Setup(service => service.GetCommitAsync("token", "tester", "demo", "deadbeef", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubCommitInfo("deadbeef", new GitHubCommitDetails("setup commit")));

        var orchestrator = CreateOrchestrator(db, readiness, gitHub.Object);
        await orchestrator.TriggerTargetAsync(projectId, userId, vercelTargetId, "main", CancellationToken.None);

        var saved = await db.Deployments.SingleAsync();
        Assert.Equal("deadbeef", saved.GitCommitSha);
        Assert.Equal("setup commit", saved.GitCommitMessage);
    }

    private static DeploymentOrchestrator CreateOrchestrator(
        DeployAIDbContext db,
        DeploymentReadinessResult readiness,
        IGitHubService? gitHubService = null)
    {
        var readinessService = new Mock<IDeploymentReadinessService>();
        readinessService
            .Setup(service => service.ScanProjectAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(readiness);

        var gitHub = gitHubService ?? new Mock<IGitHubService>().Object;
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        var backgroundJobs = new Mock<IBackgroundJobClient>();
        backgroundJobs
            .Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns("job-id");

        return new DeploymentOrchestrator(
            db,
            backgroundJobs.Object,
            readinessService.Object,
            gitHub,
            encryption.Object);
    }
}
