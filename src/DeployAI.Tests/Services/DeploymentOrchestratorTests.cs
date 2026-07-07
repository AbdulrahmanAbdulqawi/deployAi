using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
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

public class DeploymentOrchestratorTests
{
    private static DeploymentOrchestrator CreateOrchestrator(
        DeployAIDbContext db,
        IBackgroundJobClient backgroundJobs,
        DeploymentReadinessResult? readiness = null)
    {
        var readinessService = new Mock<IDeploymentReadinessService>();
        readinessService
            .Setup(service => service.ScanProjectAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(readiness ?? new DeploymentReadinessResult(true, "abc123", false, [], []));

        var gitHubService = new Mock<IGitHubService>();
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        return new DeploymentOrchestrator(
            db,
            backgroundJobs,
            readinessService.Object,
            gitHubService.Object,
            encryption.Object);
    }
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

        IDeploymentOrchestrator orchestrator = CreateOrchestrator(db, backgroundJobs.Object);
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

        IDeploymentOrchestrator orchestrator = CreateOrchestrator(db, backgroundJobs.Object);
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

        IDeploymentOrchestrator orchestrator = CreateOrchestrator(db, backgroundJobs.Object);
        var result = await orchestrator.TriggerAsync(projectId, userId, "main", CancellationToken.None);

        Assert.Single(result.Targets);
        backgroundJobs.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);

        var saved = await db.Deployments.Include(d => d.Targets).SingleAsync();
        Assert.Single(saved.Targets);
        Assert.Equal(serverTargetId, saved.Targets.First().DeployTargetId);
    }

    [Fact]
    public void OrderDeploymentTargetIds_PlacesServerBeforeWebsite()
    {
        var projectId = Guid.NewGuid();
        var vercelTargetId = Guid.NewGuid();
        var railwayTargetId = Guid.NewGuid();
        var vercelDeploymentTargetId = Guid.NewGuid();
        var railwayDeploymentTargetId = Guid.NewGuid();

        var project = new Project
        {
            Id = projectId,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = vercelTargetId,
                    ProviderName = "vercel",
                    ConfigJson = """{"role":"website"}"""
                },
                new DeployTarget
                {
                    Id = railwayTargetId,
                    ProviderName = "railway",
                    ConfigJson = """{"role":"server"}"""
                }
            ]
        };

        var deployment = new Deployment
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Targets =
            [
                new DeploymentTarget
                {
                    Id = vercelDeploymentTargetId,
                    DeployTargetId = vercelTargetId,
                    ProviderName = "vercel"
                },
                new DeploymentTarget
                {
                    Id = railwayDeploymentTargetId,
                    DeployTargetId = railwayTargetId,
                    ProviderName = "railway"
                }
            ]
        };

        var ordered = DeploymentOrchestrator.OrderDeploymentTargetIds(
            deployment,
            project,
            [vercelDeploymentTargetId, railwayDeploymentTargetId]);

        Assert.Equal(railwayDeploymentTargetId, ordered[0]);
        Assert.Equal(vercelDeploymentTargetId, ordered[1]);
    }

    [Fact]
    public async Task TriggerTargetAsync_CreatesSingleTargetDeployment()
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

        var backgroundJobs = new Mock<IBackgroundJobClient>();
        backgroundJobs
            .Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns("job-id");

        IDeploymentOrchestrator orchestrator = CreateOrchestrator(db, backgroundJobs.Object);
        var result = await orchestrator.TriggerTargetAsync(projectId, userId, vercelTargetId, "main", CancellationToken.None);

        Assert.Equal(DeploymentStatuses.Pending, result.Status);
        Assert.Single(result.Targets);
        Assert.Equal("vercel", result.Targets[0].ProviderName);
        backgroundJobs.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);

        var saved = await db.Deployments.Include(d => d.Targets).SingleAsync();
        Assert.Single(saved.Targets);
        Assert.Equal(vercelTargetId, saved.Targets.Single().DeployTargetId);
    }
}
