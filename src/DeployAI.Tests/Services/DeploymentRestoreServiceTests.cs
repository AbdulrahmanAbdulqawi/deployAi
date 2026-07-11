using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class DeploymentRestoreServiceTests
{
    [Fact]
    public async Task RestorePreviousAsync_WhenDeploymentNotFound_ThrowsNotFound()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DeployAIException>(() =>
            service.RestorePreviousAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("not_found", ex.ErrorCode);
    }

    [Fact]
    public async Task RestorePreviousAsync_WhenDeploymentSucceeded_ThrowsInvalidState()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        SeedProject(db, userId, projectId);
        db.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            ProjectId = projectId,
            Branch = "main",
            TriggeredBy = "user",
            Status = DeploymentStatuses.Success,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var ex = await Assert.ThrowsAsync<DeployAIException>(() =>
            service.RestorePreviousAsync(deploymentId, userId, CancellationToken.None));

        Assert.Equal("invalid_state", ex.ErrorCode);
    }

    [Fact]
    public async Task RestorePreviousAsync_WhenNoPreviousSuccess_ThrowsNoPrevious()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        SeedProject(db, userId, projectId);
        db.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            ProjectId = projectId,
            Branch = "main",
            TriggeredBy = "user",
            Status = DeploymentStatuses.Failed,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var ex = await Assert.ThrowsAsync<DeployAIException>(() =>
            service.RestorePreviousAsync(deploymentId, userId, CancellationToken.None));

        Assert.Equal("no_previous", ex.ErrorCode);
    }

    [Fact]
    public async Task RestorePreviousAsync_WhenRailwayTargetFailed_RollsBackToPreviousDeployment()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var deployTargetId = Guid.NewGuid();
        var previousDeploymentId = Guid.NewGuid();
        var failedDeploymentId = Guid.NewGuid();
        var previousProviderDeploymentId = "dep_prev";
        var rollbackCalled = false;

        SeedProject(db, userId, projectId, credentialId, deployTargetId, "railway");

        db.Deployments.Add(new Deployment
        {
            Id = previousDeploymentId,
            ProjectId = projectId,
            Branch = "main",
            TriggeredBy = "user",
            Status = DeploymentStatuses.Success,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Targets =
            [
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = previousDeploymentId,
                    DeployTargetId = deployTargetId,
                    ProviderName = "railway",
                    ProviderDeploymentId = previousProviderDeploymentId,
                    Status = DeploymentStatuses.Success,
                    DeployUrl = "https://api.example.com"
                }
            ]
        });

        db.Deployments.Add(new Deployment
        {
            Id = failedDeploymentId,
            ProjectId = projectId,
            Branch = "main",
            TriggeredBy = "user",
            Status = DeploymentStatuses.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
            Targets =
            [
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = failedDeploymentId,
                    DeployTargetId = deployTargetId,
                    ProviderName = "railway",
                    ProviderDeploymentId = "dep_failed",
                    Status = DeploymentStatuses.Failed
                }
            ]
        });
        await db.SaveChangesAsync();

        var serviceOps = new Mock<IProviderServiceOperations>();
        serviceOps.SetupGet(ops => ops.ProviderName).Returns("railway");
        serviceOps
            .Setup(ops => ops.RollbackDeploymentAsync(
                It.IsAny<ProviderCredentials>(),
                previousProviderDeploymentId,
                It.IsAny<CancellationToken>()))
            .Callback(() => rollbackCalled = true)
            .Returns(Task.CompletedTask);

        var serviceOpsFactory = new Mock<IProviderServiceOperationsFactory>();
        serviceOpsFactory.Setup(factory => factory.GetServiceOperations("railway")).Returns(serviceOps.Object);

        var service = CreateService(db, serviceOpsFactory: serviceOpsFactory.Object);
        var result = await service.RestorePreviousAsync(failedDeploymentId, userId, CancellationToken.None);

        Assert.True(rollbackCalled);
        Assert.Equal(DeploymentStatuses.Success, result.Status);
        Assert.Single(result.Targets);
        Assert.Equal("restored", result.Targets[0].Status);

        var updatedTarget = await db.Deployments
            .Include(d => d.Targets)
            .Where(d => d.Id == failedDeploymentId)
            .SelectMany(d => d.Targets)
            .FirstAsync();
        Assert.Equal(DeploymentStatuses.Success, updatedTarget.Status);
        Assert.Equal("https://api.example.com", updatedTarget.DeployUrl);
    }

    private static DeployAIDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeployAIDbContext(options);
    }

    private static void SeedProject(
        DeployAIDbContext db,
        Guid userId,
        Guid projectId,
        Guid? credentialId = null,
        Guid? deployTargetId = null,
        string providerName = "vercel")
    {
        var resolvedCredentialId = credentialId ?? Guid.NewGuid();
        var resolvedDeployTargetId = deployTargetId ?? Guid.NewGuid();

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
            Id = resolvedCredentialId,
            UserId = userId,
            ProviderName = providerName,
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
                    Id = resolvedDeployTargetId,
                    ProjectId = projectId,
                    ProviderName = providerName,
                    CredentialId = resolvedCredentialId,
                    ProviderProjectId = "svc_1",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });
    }

    private static DeploymentRestoreService CreateService(
        DeployAIDbContext db,
        IProviderServiceOperationsFactory? serviceOpsFactory = null)
    {
        var providerFactory = new Mock<IProviderFactory>();
        var serviceOps = serviceOpsFactory ?? CreateDefaultServiceOpsFactory();
        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens
            .Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        return new DeploymentRestoreService(
            db,
            providerFactory.Object,
            serviceOps,
            tokens.Object);
    }

    private static IProviderServiceOperationsFactory CreateDefaultServiceOpsFactory()
    {
        var factory = new Mock<IProviderServiceOperationsFactory>();
        factory.Setup(f => f.GetServiceOperations(It.IsAny<string>())).Returns((IProviderServiceOperations?)null);
        return factory.Object;
    }
}
