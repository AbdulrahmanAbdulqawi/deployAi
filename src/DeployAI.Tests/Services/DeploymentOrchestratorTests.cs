using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class DeploymentOrchestratorTests
{
    [Fact]
    public async Task TriggerAsync_CreatesDeploymentAndEnqueuesJobs()
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
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        await db.SaveChangesAsync();

        var backgroundJobs = new Mock<IBackgroundJobClient>();
        backgroundJobs
            .Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns("job-id");

        IDeploymentOrchestrator orchestrator = new DeploymentOrchestrator(db, backgroundJobs.Object);
        var result = await orchestrator.TriggerAsync(projectId, userId, "main", CancellationToken.None);

        Assert.Equal(DeploymentStatuses.Pending, result.Status);
        Assert.Single(result.Targets);
        backgroundJobs.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);

        var saved = await db.Deployments.Include(d => d.Targets).SingleAsync();
        Assert.Equal(projectId, saved.ProjectId);
        Assert.Single(saved.Targets);
    }

    [Fact]
    public async Task TriggerAsync_EnqueuesJobPerTarget_ForDualTargetProject()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        var vercelCredentialId = Guid.NewGuid();
        var railwayCredentialId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.ProviderCredentials.AddRange(
            new ProviderCredential
            {
                Id = vercelCredentialId,
                UserId = userId,
                ProviderName = "vercel",
                Label = "Vercel",
                TokenEncrypted = [1, 2, 3],
                CreatedAt = DateTimeOffset.UtcNow
            },
            new ProviderCredential
            {
                Id = railwayCredentialId,
                UserId = userId,
                ProviderName = "railway",
                Label = "Railway",
                TokenEncrypted = [4, 5, 6],
                CreatedAt = DateTimeOffset.UtcNow
            });

        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "Full stack",
            GitHubRepoFullName = "tester/full-stack",
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
                    CredentialId = vercelCredentialId,
                    ProviderProjectId = "website",
                    ConfigJson = """{"rootDirectory":"client","role":"website"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    ProviderName = "railway",
                    CredentialId = railwayCredentialId,
                    ProviderProjectId = "svc_1|env_1",
                    ConfigJson = """{"rootDirectory":"src/api","role":"server"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        await db.SaveChangesAsync();

        var backgroundJobs = new Mock<IBackgroundJobClient>();
        backgroundJobs
            .Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns("job-id");

        IDeploymentOrchestrator orchestrator = new DeploymentOrchestrator(db, backgroundJobs.Object);
        var result = await orchestrator.TriggerAsync(projectId, userId, "main", CancellationToken.None);

        Assert.Equal(2, result.Targets.Count);
        backgroundJobs.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Exactly(2));

        var saved = await db.Deployments.Include(d => d.Targets).SingleAsync();
        Assert.Equal(2, saved.Targets.Count);
        Assert.Contains(saved.Targets, t => t.ProviderName == "vercel");
        Assert.Contains(saved.Targets, t => t.ProviderName == "railway");
    }

    [Fact]
    public async Task TriggerAsync_SkipsDatabaseTargets()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        var railwayCredentialId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var serverTargetId = Guid.NewGuid();
        var postgresTargetId = Guid.NewGuid();

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
            Id = railwayCredentialId,
            UserId = userId,
            ProviderName = "railway",
            Label = "Railway",
            TokenEncrypted = [4, 5, 6],
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "API",
            GitHubRepoFullName = "tester/api",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = serverTargetId,
                    ProjectId = projectId,
                    ProviderName = "railway",
                    CredentialId = railwayCredentialId,
                    ProviderProjectId = "svc_1|env_1",
                    ConfigJson = """{"rootDirectory":"src/api","role":"server"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = postgresTargetId,
                    ProjectId = projectId,
                    ProviderName = "railway",
                    CredentialId = railwayCredentialId,
                    ProviderProjectId = "svc_pg|env_1",
                    ConfigJson = """{"role":"database","databaseEngine":"postgres","linkedServiceName":"Postgres"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        await db.SaveChangesAsync();

        var backgroundJobs = new Mock<IBackgroundJobClient>();
        backgroundJobs
            .Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns("job-id");

        IDeploymentOrchestrator orchestrator = new DeploymentOrchestrator(db, backgroundJobs.Object);
        var result = await orchestrator.TriggerAsync(projectId, userId, "main", CancellationToken.None);

        Assert.Single(result.Targets);
        backgroundJobs.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);

        var saved = await db.Deployments.Include(d => d.Targets).SingleAsync();
        Assert.Single(saved.Targets);
        Assert.Equal(serverTargetId, saved.Targets.First().DeployTargetId);
    }
}
