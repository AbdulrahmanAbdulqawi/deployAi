using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class FrontendEnvironmentWiringServiceTests
{
    [Fact]
    public async Task WireWebsiteTargetBeforeDeployAsync_UpsertsEnvVarsAndProxyRoutes()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDualTargetDeploymentAsync(db);
        var websiteTarget = await db.DeploymentTargets
            .FirstAsync(t => t.ProviderName == "vercel");

        var vercelManagement = new Mock<IProviderManagement>();
        vercelManagement.SetupGet(m => m.ProviderName).Returns("vercel");
        var proxy = vercelManagement.As<IWebsiteApiProxySupport>();
        proxy.Setup(p => p.EnsureApiProxyRoutesAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                "https://api.example.com",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var railwayManagement = new Mock<IProviderManagement>();
        railwayManagement.SetupGet(m => m.ProviderName).Returns("railway");

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement("vercel")).Returns(vercelManagement.Object);
        factory.Setup(f => f.GetManagement("railway")).Returns(railwayManagement.Object);

        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var service = new FrontendEnvironmentWiringService(
            db,
            factory.Object,
            serviceOperationsFactory.Object,
            tokens.Object,
            new TestHttpClientFactory());

        await service.WireWebsiteTargetBeforeDeployAsync(deploymentId, websiteTarget, CancellationToken.None);

        vercelManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "prj_web",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "API_URL" && r.Value == "https://api.example.com"),
            It.IsAny<CancellationToken>()), Times.Once);
        proxy.Verify(p => p.EnsureApiProxyRoutesAsync(
            It.IsAny<ProviderCredentials>(),
            "prj_web",
            "https://api.example.com",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WireServerTargetAfterWebsiteDeployAsync_UpsertsRailwayCorsVars()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDualTargetDeploymentAsync(db);
        var websiteTarget = await db.DeploymentTargets.FirstAsync(t => t.ProviderName == "vercel");
        websiteTarget.Status = DeploymentStatuses.Success;
        websiteTarget.DeployUrl = "https://deployai-mu.vercel.app";
        await db.SaveChangesAsync();

        var vercelManagement = new Mock<IProviderManagement>();
        vercelManagement.SetupGet(m => m.ProviderName).Returns("vercel");
        vercelManagement.As<IWebsiteApiProxySupport>()
            .Setup(p => p.ResolvePublicWebsiteUrlAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                websiteTarget.DeployUrl,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://deployai-mu.vercel.app");

        var railwayManagement = new Mock<IProviderManagement>();
        railwayManagement.SetupGet(m => m.ProviderName).Returns("railway");

        var railwayOperations = new Mock<IProviderServiceOperations>();
        railwayOperations.SetupGet(o => o.ProviderName).Returns("railway");

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement("vercel")).Returns(vercelManagement.Object);
        factory.Setup(f => f.GetManagement("railway")).Returns(railwayManagement.Object);

        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();
        serviceOperationsFactory.Setup(f => f.GetServiceOperations("railway")).Returns(railwayOperations.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var service = new FrontendEnvironmentWiringService(
            db,
            factory.Object,
            serviceOperationsFactory.Object,
            tokens.Object,
            new TestHttpClientFactory());

        await service.WireServerTargetAfterWebsiteDeployAsync(deploymentId, websiteTarget, CancellationToken.None);

        railwayManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "svc_api|env_1",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "App__FrontendUrl" && r.Value == "https://deployai-mu.vercel.app"),
            It.IsAny<CancellationToken>()), Times.Once);
        railwayOperations.Verify(o => o.RedeployServiceAsync(
            It.IsAny<ProviderCredentials>(),
            "svc_api|env_1",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static DeployAIDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeployAIDbContext(options);
    }

    private static async Task<Guid> SeedDualTargetDeploymentAsync(DeployAIDbContext db)
    {
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var vercelCredentialId = Guid.NewGuid();
        var railwayCredentialId = Guid.NewGuid();
        var vercelTargetId = Guid.NewGuid();
        var railwayTargetId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();

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
                TokenEncrypted = [1],
                CreatedAt = DateTimeOffset.UtcNow
            },
            new ProviderCredential
            {
                Id = railwayCredentialId,
                UserId = userId,
                ProviderName = "railway",
                Label = "Railway",
                TokenEncrypted = [2],
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
                    CredentialId = vercelCredentialId,
                    ProviderProjectId = "prj_web",
                    ConfigJson = """{"role":"website","framework":"angular"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = railwayTargetId,
                    ProjectId = projectId,
                    ProviderName = "railway",
                    CredentialId = railwayCredentialId,
                    ProviderProjectId = "svc_api|env_1",
                    ConfigJson = """{"role":"server","framework":"dotnet"}""",
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
            Status = DeploymentStatuses.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            Targets =
            [
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = deploymentId,
                    DeployTargetId = railwayTargetId,
                    ProviderName = "railway",
                    Status = DeploymentStatuses.Success,
                    DeployUrl = "https://api.example.com"
                },
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = deploymentId,
                    DeployTargetId = vercelTargetId,
                    ProviderName = "vercel",
                    Status = DeploymentStatuses.InProgress
                }
            ]
        });
        await db.SaveChangesAsync();
        return deploymentId;
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
