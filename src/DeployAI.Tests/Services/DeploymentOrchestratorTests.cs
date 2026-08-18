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
    public void OrderDeploymentTargetIds_PlacesCoolifyServerBeforeWebsite()
    {
        var projectId = Guid.NewGuid();
        var websiteTargetId = Guid.NewGuid();
        var serverTargetId = Guid.NewGuid();
        var websiteDeploymentTargetId = Guid.NewGuid();
        var serverDeploymentTargetId = Guid.NewGuid();

        var project = new Project
        {
            Id = projectId,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = websiteTargetId,
                    ProviderName = "coolify",
                    ConfigJson = """{"role":"website"}"""
                },
                new DeployTarget
                {
                    Id = serverTargetId,
                    ProviderName = "coolify",
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
                    Id = websiteDeploymentTargetId,
                    DeployTargetId = websiteTargetId,
                    ProviderName = "coolify"
                },
                new DeploymentTarget
                {
                    Id = serverDeploymentTargetId,
                    DeployTargetId = serverTargetId,
                    ProviderName = "coolify"
                }
            ]
        };

        var ordered = DeploymentOrchestrator.OrderDeploymentTargetIds(
            deployment,
            project,
            [websiteDeploymentTargetId, serverDeploymentTargetId]);

        Assert.Equal(serverDeploymentTargetId, ordered[0]);
        Assert.Equal(websiteDeploymentTargetId, ordered[1]);
    }

    [Theory]
    // Mirqab's compose app never got a domain at creation -- Coolify rejects one before the
    // first deploy -- and its top-level fqdn stayed empty forever after, because nothing ever
    // assigned one. Traefik routes a compose app off docker_compose_domains, never fqdn, so the
    // site 404'd at the proxy despite both containers starting cleanly. This is the gate deciding
    // when the post-deploy call that fixes that should attempt to run; it does not wait for a URL
    // to already exist; the provider derives its own default.
    [InlineData("coolify", true, true)]
    // Not compose: a single-app Coolify deploy's fqdn already routes correctly on its own.
    [InlineData("coolify", false, false)]
    // Not Coolify: no other provider needs this per-service domain call.
    [InlineData("vercel", true, false)]
    public void ShouldAssignComposeDomain_OnlyForACoolifyComposeTarget(
        string providerName, bool isCompose, bool expected)
    {
        var config = DeployTargetConfig.Parse(isCompose
            ? """{"role":"website","composeFileLocation":"docker-compose.coolify.yml"}"""
            : """{"role":"website"}""");

        var result = DeploymentJobRunner.ShouldAssignComposeDomain(providerName, config);

        Assert.Equal(expected, result);
    }

    // The wizard shows its domain field only on compose plans, and a compose app is exactly the
    // one that cannot be given a domain at creation -- so the typed value had to survive until the
    // post-deploy hook, and nothing persisted it. Every compose app therefore came up on sslip.io
    // no matter what its owner asked for, with no error to say so.
    [Fact]
    public void ResolveComposeDomain_UsesTheDomainTheUserTyped_NotSslip()
    {
        var config = DeployTargetConfig.Parse(
            """
            {"role":"website","composeFileLocation":"docker-compose.coolify.yml","customDomain":"breeze.example.com"}
            """);

        Assert.Equal("http://breeze.example.com", DeploymentJobRunner.ResolveComposeDomain(config));
    }

    // Coolify upgrades a scheme-less domain to https:// on the way in, and an https:// FQDN makes
    // Traefik attempt an ACME challenge immediately. Nothing checks DNS yet, so that challenge
    // would fail against a domain not yet pointed here, spend one of Let's Encrypt's five failed
    // validations an hour, and leave a self-signed certificate behind a green deploy. Until the
    // DNS gate exists, the scheme is stated rather than inferred.
    [Theory]
    [InlineData("breeze.example.com")]
    [InlineData("http://breeze.example.com")]
    [InlineData("https://breeze.example.com")]
    [InlineData("  breeze.example.com/  ")]
    public void ResolveComposeDomain_KeepsTheHttpScheme_SoNoCertificateIsRequestedBeforeDnsIsChecked(
        string typed)
    {
        var config = new DeployTargetConfig { CustomDomain = typed };

        Assert.Equal("http://breeze.example.com", DeploymentJobRunner.ResolveComposeDomain(config));
    }

    // Null is what lets the provider fall back to its own sslip.io convention, so "the user typed
    // nothing" has to stay distinguishable from "the user typed something".
    [Theory]
    [InlineData("""{"role":"website","composeFileLocation":"c.yml"}""")]
    [InlineData("""{"role":"website","composeFileLocation":"c.yml","customDomain":""}""")]
    [InlineData("""{"role":"website","composeFileLocation":"c.yml","customDomain":"   "}""")]
    public void ResolveComposeDomain_IsNull_WhenNoDomainWasTyped(string configJson)
    {
        Assert.Null(DeploymentJobRunner.ResolveComposeDomain(DeployTargetConfig.Parse(configJson)));
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
