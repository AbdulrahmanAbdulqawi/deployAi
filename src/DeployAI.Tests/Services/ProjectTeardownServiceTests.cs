using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

public class ProjectTeardownServiceTests
{
    [Fact]
    public async Task TeardownAsync_CancelsInFlightDeployments_AndRemovesProject()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
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
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var credential = new ProviderCredential
        {
            Id = credentialId,
            UserId = userId,
            ProviderName = "vercel",
            Label = "Default",
            TokenEncrypted = [1, 2, 3],
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ProviderCredentials.Add(credential);

        var deployTarget = new DeployTarget
        {
            Id = deployTargetId,
            ProjectId = projectId,
            ProviderName = "vercel",
            CredentialId = credentialId,
            ProviderProjectId = "prj_vercel",
            CreatedAt = DateTimeOffset.UtcNow,
            Credential = credential
        };

        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "Demo",
            GitHubRepoFullName = "tester/demo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeployTargets = [deployTarget],
            Deployments =
            [
                new Deployment
                {
                    Id = deploymentId,
                    ProjectId = projectId,
                    Branch = "main",
                    Status = DeploymentStatuses.InProgress,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Targets =
                    [
                        new DeploymentTarget
                        {
                            Id = deploymentTargetId,
                            DeploymentId = deploymentId,
                            DeployTargetId = deployTargetId,
                            ProviderName = "vercel",
                            Status = DeploymentStatuses.InProgress,
                            DeployTarget = deployTarget
                        }
                    ]
                }
            ]
        });

        await db.SaveChangesAsync();

        var railwayProvisioning = new Mock<IRailwayDatabaseProvisioningService>();
        var management = new Mock<IProviderManagement>();
        management
            .Setup(m => m.DeleteProjectAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_vercel",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var managementFactory = new Mock<IProviderManagementFactory>();
        managementFactory.Setup(f => f.GetManagement("vercel")).Returns(management.Object);

        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();
        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens
            .Setup(t => t.GetTokenAsync(credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync("vercel-token");

        var service = new ProjectTeardownService(
            db,
            railwayProvisioning.Object,
            managementFactory.Object,
            serviceOperationsFactory.Object,
            tokens.Object,
            NullLogger<ProjectTeardownService>.Instance);

        await service.TeardownAsync(projectId, userId, CancellationToken.None);

        Assert.False(await db.Projects.AnyAsync(p => p.Id == projectId));
        Assert.False(await db.Deployments.AnyAsync(d => d.Id == deploymentId));

        management.Verify(
            m => m.DeleteProjectAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_vercel",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TeardownAsync_ThrowsNotFound_WhenProjectMissing()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var service = new ProjectTeardownService(
            db,
            Mock.Of<IRailwayDatabaseProvisioningService>(),
            Mock.Of<IProviderManagementFactory>(),
            Mock.Of<IProviderServiceOperationsFactory>(),
            Mock.Of<IProviderCredentialTokenService>(),
            NullLogger<ProjectTeardownService>.Instance);

        await Assert.ThrowsAsync<DeployAIException>(() =>
            service.TeardownAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task TeardownAsync_DeletesCoolifyApplicationsAndDatabases()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var websiteTargetId = Guid.NewGuid();
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

        var credential = new ProviderCredential
        {
            Id = credentialId,
            UserId = userId,
            ProviderName = "coolify",
            Label = "Coolify",
            TokenEncrypted = [1, 2, 3],
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ProviderCredentials.Add(credential);

        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "Coolify stack",
            GitHubRepoFullName = "tester/stack",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = websiteTargetId,
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    CredentialId = credentialId,
                    ProviderProjectId = "app_web",
                    ConfigJson = """{"role":"website","framework":"angular"}""",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Credential = credential
                },
                new DeployTarget
                {
                    Id = serverTargetId,
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    CredentialId = credentialId,
                    ProviderProjectId = "app_api",
                    ConfigJson = """{"role":"server","framework":"dotnet"}""",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Credential = credential
                },
                new DeployTarget
                {
                    Id = postgresTargetId,
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    CredentialId = credentialId,
                    ProviderProjectId = "db_pg|env_1",
                    ConfigJson = """{"role":"database","databaseEngine":"postgres"}""",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Credential = credential
                }
            ]
        });

        await db.SaveChangesAsync();

        var railwayProvisioning = new Mock<IRailwayDatabaseProvisioningService>();
        railwayProvisioning
            .Setup(p => p.TeardownDatabaseServiceOnProviderAsync(
                It.IsAny<Project>(),
                It.IsAny<DeployTarget>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var coolifyOperations = new Mock<IProviderServiceOperations>();
        coolifyOperations
            .Setup(o => o.DeleteServiceAsync(
                It.IsAny<ProviderCredentials>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();
        serviceOperationsFactory.Setup(f => f.GetServiceOperations("coolify")).Returns(coolifyOperations.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens
            .Setup(t => t.GetTokenAsync(credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync("coolify-token");

        var service = new ProjectTeardownService(
            db,
            railwayProvisioning.Object,
            Mock.Of<IProviderManagementFactory>(),
            serviceOperationsFactory.Object,
            tokens.Object,
            NullLogger<ProjectTeardownService>.Instance);

        await service.TeardownAsync(projectId, userId, CancellationToken.None);

        Assert.False(await db.Projects.AnyAsync(p => p.Id == projectId));
        railwayProvisioning.Verify(
            p => p.TeardownDatabaseServiceOnProviderAsync(
                It.IsAny<Project>(),
                It.Is<DeployTarget>(t => t.ProviderProjectId == "db_pg|env_1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        coolifyOperations.Verify(
            o => o.DeleteServiceAsync(It.IsAny<ProviderCredentials>(), "app_web", It.IsAny<CancellationToken>()),
            Times.Once);
        coolifyOperations.Verify(
            o => o.DeleteServiceAsync(It.IsAny<ProviderCredentials>(), "app_api", It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
