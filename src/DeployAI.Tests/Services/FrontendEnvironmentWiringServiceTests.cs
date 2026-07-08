using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class FrontendEnvironmentWiringServiceTests
{
    [Fact]
    public async Task WireWebsiteTargetBeforeDeployAsync_UpsertsSplitOriginEnvVars()
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

        var service = CreateService(db, factory, serviceOperationsFactory, tokens);

        await service.WireWebsiteTargetBeforeDeployAsync(deploymentId, websiteTarget, CancellationToken.None);

        vercelManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "prj_web",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "DEPLOYAI_API_URL" && r.Value == "https://api.example.com"),
            It.IsAny<CancellationToken>()), Times.Once);
        vercelManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "prj_web",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "API_BASE_URL" && r.Value == "https://api.example.com"),
            It.IsAny<CancellationToken>()), Times.Once);
        vercelManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "prj_web",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "NG_APP_API_URL" && r.Value == "https://api.example.com"),
            It.IsAny<CancellationToken>()), Times.Once);
        proxy.Verify(p => p.EnsureApiProxyRoutesAsync(
            It.IsAny<ProviderCredentials>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
        vercelManagement.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        vercelManagement.As<IWebsiteApiProxySupport>()
            .Setup(p => p.ResolvePublicWebsiteUrlAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                "https://deployai-mu.vercel.app",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://deployai-mu.vercel.app");

        var railwayManagement = new Mock<IProviderManagement>();
        railwayManagement.SetupGet(m => m.ProviderName).Returns("railway");
        railwayManagement.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var railwayOperations = new Mock<IProviderServiceOperations>();
        railwayOperations.SetupGet(o => o.ProviderName).Returns("railway");
        railwayOperations.Setup(o => o.GetServiceStatusAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderServiceStatus("running", "https://api.example.com", null));
        railwayOperations.Setup(o => o.RedeployServiceAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement("vercel")).Returns(vercelManagement.Object);
        factory.Setup(f => f.GetManagement("railway")).Returns(railwayManagement.Object);

        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();
        serviceOperationsFactory.Setup(f => f.GetServiceOperations("railway")).Returns(railwayOperations.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var service = CreateService(db, factory, serviceOperationsFactory, tokens);

        await service.WireServerTargetAfterWebsiteDeployAsync(deploymentId, websiteTarget, CancellationToken.None);

        railwayManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "svc_api|env_1",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "App__FrontendUrl" && r.Value == "https://deployai-mu.vercel.app"),
            It.IsAny<CancellationToken>()), Times.Once);
        railwayManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "svc_api|env_1",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "AllowedOrigins__0" && r.Value == "https://deployai-mu.vercel.app"),
            It.IsAny<CancellationToken>()), Times.Once);
        railwayManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "svc_api|env_1",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "App__BaseUrl" && r.Value == "https://deployai-mu.vercel.app"),
            It.IsAny<CancellationToken>()), Times.Once);
        railwayOperations.Verify(o => o.RedeployServiceAsync(
            It.IsAny<ProviderCredentials>(),
            "svc_api|env_1",
            It.IsAny<CancellationToken>()), Times.Once);
        vercelManagement.As<IWebsiteApiProxySupport>().Verify(p => p.EnsureApiProxyRoutesAsync(
            It.IsAny<ProviderCredentials>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WireWebsiteTargetBeforeDeployAsync_UsesLiveRailwayUrl_WhenCurrentDeploymentHasNoApiUrl()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDualTargetDeploymentAsync(db);
        var deployment = await db.Deployments.Include(d => d.Targets).FirstAsync(d => d.Id == deploymentId);
        var railwayTarget = deployment.Targets.First(t => t.ProviderName == "railway");
        railwayTarget.DeployUrl = null;
        railwayTarget.Status = DeploymentStatuses.InProgress;
        await db.SaveChangesAsync();

        var websiteTarget = deployment.Targets.First(t => t.ProviderName == "vercel");

        var vercelManagement = new Mock<IProviderManagement>();
        vercelManagement.SetupGet(m => m.ProviderName).Returns("vercel");
        var proxy = vercelManagement.As<IWebsiteApiProxySupport>();
        proxy.Setup(p => p.EnsureApiProxyRoutesAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                "https://live-api.example.com",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var railwayManagement = new Mock<IProviderManagement>();
        railwayManagement.SetupGet(m => m.ProviderName).Returns("railway");

        var railwayOperations = new Mock<IProviderServiceOperations>();
        railwayOperations.SetupGet(o => o.ProviderName).Returns("railway");
        railwayOperations.Setup(o => o.GetServiceStatusAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderServiceStatus("running", "https://live-api.example.com", null));

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement("vercel")).Returns(vercelManagement.Object);
        factory.Setup(f => f.GetManagement("railway")).Returns(railwayManagement.Object);

        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();
        serviceOperationsFactory.Setup(f => f.GetServiceOperations("railway")).Returns(railwayOperations.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var service = CreateService(db, factory, serviceOperationsFactory, tokens);

        await service.WireWebsiteTargetBeforeDeployAsync(deploymentId, websiteTarget, CancellationToken.None);

        vercelManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "prj_web",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "NG_APP_API_URL" && r.Value == "https://live-api.example.com"),
            It.IsAny<CancellationToken>()), Times.Once);
        proxy.Verify(p => p.EnsureApiProxyRoutesAsync(
            It.IsAny<ProviderCredentials>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WireServerTargetBeforeRailwayDeployAsync_UpsertsCorsFromLastWebsiteUrl()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDualTargetDeploymentAsync(db);
        var project = await db.Projects.Include(p => p.DeployTargets).FirstAsync();
        var vercelDeployTargetId = project.DeployTargets.First(t => t.ProviderName == "vercel").Id;

        db.Deployments.Add(new Deployment
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Branch = "main",
            TriggeredBy = "user",
            Status = DeploymentStatuses.Success,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-1),
            Targets =
            [
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = Guid.Empty,
                    DeployTargetId = vercelDeployTargetId,
                    ProviderName = "vercel",
                    Status = DeploymentStatuses.Success,
                    DeployUrl = "https://idaara-livid.vercel.app",
                    CompletedAt = DateTimeOffset.UtcNow.AddDays(-1)
                }
            ]
        });
        var priorDeployment = db.ChangeTracker.Entries<Deployment>().Last().Entity;
        priorDeployment.Targets.First().DeploymentId = priorDeployment.Id;
        await db.SaveChangesAsync();

        var railwayTarget = await db.DeploymentTargets.FirstAsync(t => t.ProviderName == "railway");

        var vercelManagement = new Mock<IProviderManagement>();
        vercelManagement.SetupGet(m => m.ProviderName).Returns("vercel");
        vercelManagement.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        vercelManagement.As<IWebsiteApiProxySupport>()
            .Setup(p => p.ResolvePublicWebsiteUrlAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                "https://idaara-livid.vercel.app",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://idaara-livid.vercel.app");

        var railwayManagement = new Mock<IProviderManagement>();
        railwayManagement.SetupGet(m => m.ProviderName).Returns("railway");
        railwayManagement.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var railwayOperations = new Mock<IProviderServiceOperations>();
        railwayOperations.SetupGet(o => o.ProviderName).Returns("railway");
        railwayOperations.Setup(o => o.GetServiceStatusAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderServiceStatus("running", "https://api.example.com", null));

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement("vercel")).Returns(vercelManagement.Object);
        factory.Setup(f => f.GetManagement("railway")).Returns(railwayManagement.Object);

        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();
        serviceOperationsFactory.Setup(f => f.GetServiceOperations("railway")).Returns(railwayOperations.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var service = CreateService(db, factory, serviceOperationsFactory, tokens);

        await service.WireServerTargetBeforeRailwayDeployAsync(deploymentId, railwayTarget, CancellationToken.None);

        railwayManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "svc_api|env_1",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "AllowedOrigins__0" && r.Value == "https://idaara-livid.vercel.app"),
            It.IsAny<CancellationToken>()), Times.Once);
        railwayManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "svc_api|env_1",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "App__BaseUrl" && r.Value == "https://idaara-livid.vercel.app"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncCrossProviderEnvironmentAsync_AppliesRailwayAndVercelEnvVars()
    {
        await using var db = CreateDb();
        var projectId = await SeedDualTargetProjectAsync(db);

        var vercelManagement = new Mock<IProviderManagement>();
        vercelManagement.SetupGet(m => m.ProviderName).Returns("vercel");
        vercelManagement.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        vercelManagement.As<IWebsiteApiProxySupport>()
            .Setup(p => p.ResolvePublicWebsiteUrlAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://idaara-livid.vercel.app");
        vercelManagement.As<IWebsiteApiProxySupport>()
            .Setup(p => p.ListProductionWebsiteOriginsAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                "https://idaara-livid.vercel.app",
                "https://idaara-git-master-user.vercel.app"
            ]);
        vercelManagement.As<IWebsiteApiProxySupport>()
            .Setup(p => p.EnsureApiProxyRoutesAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                "https://api.example.com",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        vercelManagement.As<IWebsiteApiProxySupport>()
            .Setup(p => p.AssignProductionDomainsToDeploymentAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var deploymentProvider = new Mock<IDeploymentProvider>();
        deploymentProvider.SetupGet(p => p.ProviderName).Returns("vercel");
        deploymentProvider.Setup(p => p.TriggerDeploymentAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentResponse("dpl_test", "https://idaara-livid.vercel.app"));

        var providerFactory = new Mock<IProviderFactory>();
        providerFactory.Setup(f => f.GetProvider("vercel")).Returns(deploymentProvider.Object);

        var railwayManagement = new Mock<IProviderManagement>();
        railwayManagement.SetupGet(m => m.ProviderName).Returns("railway");
        railwayManagement.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var railwayOperations = new Mock<IProviderServiceOperations>();
        railwayOperations.SetupGet(o => o.ProviderName).Returns("railway");
        railwayOperations.Setup(o => o.GetServiceStatusAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderServiceStatus("running", "https://api.example.com", null));

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement("vercel")).Returns(vercelManagement.Object);
        factory.Setup(f => f.GetManagement("railway")).Returns(railwayManagement.Object);

        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();
        serviceOperationsFactory.Setup(f => f.GetServiceOperations("railway")).Returns(railwayOperations.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var service = CreateService(db, factory, serviceOperationsFactory, tokens, providerFactory);

        var result = await service.SyncCrossProviderEnvironmentAsync(
            projectId,
            new EnvironmentSyncOptions(
                RedeployRailwayAfterUpdate: false,
                RunVerification: false,
                Source: "manual"),
            CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal("https://idaara-livid.vercel.app", result.ResolvedWebsiteUrl);
        Assert.Equal("https://api.example.com", result.ResolvedApiUrl);
        Assert.Contains("DEPLOYAI_API_URL", result.VercelKeysApplied);
        Assert.Contains("API_BASE_URL", result.VercelKeysApplied);
        Assert.Contains("NG_APP_API_URL", result.VercelKeysApplied);
        Assert.Contains("App__FrontendUrl", result.RailwayKeysApplied);
        vercelManagement.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(),
            "prj_web",
            It.Is<UpsertProviderEnvVarRequest>(r => r.Key == "API_URL" && r.Value == "https://api.example.com"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncCrossProviderEnvironmentAsync_DetectsDriftBeforeApply()
    {
        await using var db = CreateDb();
        var projectId = await SeedDualTargetProjectAsync(db);

        var vercelManagement = new Mock<IProviderManagement>();
        vercelManagement.SetupGet(m => m.ProviderName).Returns("vercel");
        vercelManagement.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProviderEnvVar("env_1", "API_URL", "https://wrong.example.com", "encrypted", [], false)
            ]);
        vercelManagement.As<IWebsiteApiProxySupport>()
            .Setup(p => p.ResolvePublicWebsiteUrlAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://idaara-livid.vercel.app");

        var railwayManagement = new Mock<IProviderManagement>();
        railwayManagement.SetupGet(m => m.ProviderName).Returns("railway");
        railwayManagement.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProviderEnvVar("env_1", "App__FrontendUrl", "https://idaara.vercel.app", "plain", [], false)
            ]);

        var railwayOperations = new Mock<IProviderServiceOperations>();
        railwayOperations.SetupGet(o => o.ProviderName).Returns("railway");
        railwayOperations.Setup(o => o.GetServiceStatusAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderServiceStatus("running", "https://api.example.com", null));

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement("vercel")).Returns(vercelManagement.Object);
        factory.Setup(f => f.GetManagement("railway")).Returns(railwayManagement.Object);

        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();
        serviceOperationsFactory.Setup(f => f.GetServiceOperations("railway")).Returns(railwayOperations.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var service = CreateService(db, factory, serviceOperationsFactory, tokens);

        var result = await service.SyncCrossProviderEnvironmentAsync(
            projectId,
            new EnvironmentSyncOptions(
                DetectDriftOnly: true,
                RunVerification: false,
                Source: "scheduled"),
            CancellationToken.None);

        Assert.True(result.DriftDetected);
        Assert.Contains(result.DriftDetails, detail => detail.Contains("Railway App__FrontendUrl mismatch", StringComparison.Ordinal));
        Assert.Contains(result.DriftDetails, detail => detail.Contains("Vercel API_URL mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncCrossProviderEnvironmentAsync_RedeploysVercel_WhenDeployedSpaMissingApiUrl()
    {
        await using var db = CreateDb();
        var projectId = await SeedDualTargetProjectAsync(db);
        var mocks = CreateSplitOriginSyncMocks(bundleContent: "console.log('no api url baked in');");
        mocks.Readiness.Setup(r => r.ScanProjectAsync(projectId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentReadinessResult(true, "sha", true, [], []));

        var service = CreateSplitOriginService(db, mocks);

        var result = await service.SyncCrossProviderEnvironmentAsync(
            projectId,
            new EnvironmentSyncOptions(
                RedeployRailwayAfterUpdate: false,
                RunVerification: true,
                Source: "manual"),
            CancellationToken.None);

        mocks.DeploymentProvider.Verify(p => p.TriggerDeploymentAsync(
            It.IsAny<ProviderCredentials>(),
            "prj_web",
            "main",
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(result.Success);
        Assert.Contains(result.VerificationMessages, message =>
            message.Contains("redeploy triggered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SyncCrossProviderEnvironmentAsync_ReportsBlockingFindings_WhenWiringFilesMissing()
    {
        await using var db = CreateDb();
        var projectId = await SeedDualTargetProjectAsync(db);
        var mocks = CreateSplitOriginSyncMocks(bundleContent: "console.log('no api url baked in');");
        mocks.Readiness.Setup(r => r.ScanProjectAsync(projectId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentReadinessResult(
                false,
                "sha",
                true,
                [new MissingDeploymentFile(
                    "scripts/write-api-env.mjs",
                    "Required to bake the API URL into the production build.",
                    DeploymentFileSeverity.Blocking)],
                []));

        var service = CreateSplitOriginService(db, mocks);

        var result = await service.SyncCrossProviderEnvironmentAsync(
            projectId,
            new EnvironmentSyncOptions(
                RedeployRailwayAfterUpdate: false,
                RunVerification: true,
                Source: "manual"),
            CancellationToken.None);

        mocks.DeploymentProvider.Verify(p => p.TriggerDeploymentAsync(
            It.IsAny<ProviderCredentials>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(result.Success);
        Assert.Contains(result.VerificationMessages, message =>
            message.Contains("Missing split-origin setup files", StringComparison.Ordinal) &&
            message.Contains("scripts/write-api-env.mjs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncCrossProviderEnvironmentAsync_DoesNotRedeploy_WhenDeployedSpaIsWired()
    {
        await using var db = CreateDb();
        var projectId = await SeedDualTargetProjectAsync(db);
        var mocks = CreateSplitOriginSyncMocks(bundleContent: "console.log('entry bundle');");
        // The env-baked API URL often lands in a chunk referenced via modulepreload, not <script src>.
        mocks.Http.SetResponse(
            "https://idaara-livid.vercel.app/",
            """<html><head><link rel="modulepreload" href="chunk-def456.js"><script src="main-abc123.js" type="module"></script></head><body></body></html>""",
            "text/html");
        mocks.Http.SetResponse(
            "https://idaara-livid.vercel.app/chunk-def456.js",
            "withInterceptors([apiBaseInterceptor]);apiBaseUrl:'https://api.example.com';",
            "application/javascript");

        var service = CreateSplitOriginService(db, mocks);

        var result = await service.SyncCrossProviderEnvironmentAsync(
            projectId,
            new EnvironmentSyncOptions(
                RedeployRailwayAfterUpdate: false,
                RunVerification: false,
                Source: "manual"),
            CancellationToken.None);

        Assert.False(result.Skipped);
        mocks.DeploymentProvider.Verify(p => p.TriggerDeploymentAsync(
            It.IsAny<ProviderCredentials>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        mocks.Readiness.Verify(r => r.ScanProjectAsync(
            It.IsAny<Guid>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncCrossProviderEnvironmentAsync_DriftOnly_ReportsStaleSplitOriginBuild()
    {
        await using var db = CreateDb();
        var projectId = await SeedDualTargetProjectAsync(db);
        var mocks = CreateSplitOriginSyncMocks(bundleContent: "console.log('no api url baked in');");
        mocks.Readiness.Setup(r => r.ScanProjectAsync(projectId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentReadinessResult(true, "sha", true, [], []));

        var service = CreateSplitOriginService(db, mocks);

        var result = await service.SyncCrossProviderEnvironmentAsync(
            projectId,
            new EnvironmentSyncOptions(
                DetectDriftOnly: true,
                RunVerification: false,
                Source: "scheduled"),
            CancellationToken.None);

        Assert.True(result.DriftDetected);
        Assert.Contains(result.DriftDetails, detail =>
            detail.Contains("missing split-origin bundle wiring", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SplitOriginSyncMocks(
        Mock<IProviderManagementFactory> Factory,
        Mock<IProviderServiceOperationsFactory> ServiceOperationsFactory,
        Mock<IProviderCredentialTokenService> Tokens,
        Mock<IProviderFactory> ProviderFactory,
        Mock<IDeploymentProvider> DeploymentProvider,
        Mock<IGitHubService> GitHubService,
        Mock<IDeploymentReadinessService> Readiness,
        StubHttpMessageHandler Http);

    /// <summary>
    /// Mocks a healthy split-origin project: Vercel env vars already correct (hidden values),
    /// repo vercel.json already SPA-only, so the sync reaches the deployed-SPA probe instead
    /// of redeploying for env drift or vercel.json changes.
    /// </summary>
    private static SplitOriginSyncMocks CreateSplitOriginSyncMocks(string bundleContent)
    {
        var vercelManagement = new Mock<IProviderManagement>();
        vercelManagement.SetupGet(m => m.ProviderName).Returns("vercel");
        vercelManagement.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ProviderEnvVar("env_0", "DEPLOYAI_API_URL", string.Empty, "encrypted", [], true),
                new ProviderEnvVar("env_1", "API_BASE_URL", string.Empty, "encrypted", [], true),
                new ProviderEnvVar("env_2", "IDAARA_API_URL", string.Empty, "encrypted", [], true),
                new ProviderEnvVar("env_3", "NG_APP_API_URL", string.Empty, "encrypted", [], true),
                new ProviderEnvVar("env_4", "API_URL", string.Empty, "encrypted", [], true)
            ]);
        var proxySupport = vercelManagement.As<IWebsiteApiProxySupport>();
        proxySupport.Setup(p => p.ResolvePublicWebsiteUrlAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://idaara-livid.vercel.app");
        proxySupport.Setup(p => p.ListProductionWebsiteOriginsAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["https://idaara-livid.vercel.app"]);
        proxySupport.Setup(p => p.GetLatestProductionDeploymentIdAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("dpl_current");
        proxySupport.Setup(p => p.AssignProductionDomainsToDeploymentAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var railwayManagement = new Mock<IProviderManagement>();
        railwayManagement.SetupGet(m => m.ProviderName).Returns("railway");
        railwayManagement.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var railwayOperations = new Mock<IProviderServiceOperations>();
        railwayOperations.SetupGet(o => o.ProviderName).Returns("railway");
        railwayOperations.Setup(o => o.GetServiceStatusAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api|env_1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderServiceStatus("running", "https://api.example.com", null));

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement("vercel")).Returns(vercelManagement.Object);
        factory.Setup(f => f.GetManagement("railway")).Returns(railwayManagement.Object);

        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();
        serviceOperationsFactory.Setup(f => f.GetServiceOperations("railway")).Returns(railwayOperations.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var deploymentProvider = new Mock<IDeploymentProvider>();
        deploymentProvider.SetupGet(p => p.ProviderName).Returns("vercel");
        deploymentProvider.Setup(p => p.TriggerDeploymentAsync(
                It.IsAny<ProviderCredentials>(),
                "prj_web",
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentResponse("dpl_new", "https://idaara-livid.vercel.app"));

        var providerFactory = new Mock<IProviderFactory>();
        providerFactory.Setup(f => f.GetProvider("vercel")).Returns(deploymentProvider.Object);

        // vercel.json in the repo is already the canonical SPA-only content, so
        // EnsureSplitOriginVercelJsonAsync makes no commit and no redeploy is queued for it.
        var websiteConfig = DeployTargetConfig.Parse("""{"role":"website","framework":"angular"}""");
        VercelJsonRewrites.TryBuildSpaOnlyContent(null, websiteConfig, out var spaOnlyVercelJson);
        var gitHubService = new Mock<IGitHubService>();
        gitHubService.Setup(g => g.GetFileMetadataAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubFileMetadata(spaOnlyVercelJson, "sha-existing"));

        var readiness = new Mock<IDeploymentReadinessService>();

        var http = new StubHttpMessageHandler();
        http.SetResponse(
            "https://idaara-livid.vercel.app/",
            """<html><head><script src="main-abc123.js" type="module"></script></head><body></body></html>""",
            "text/html");
        http.SetResponse("https://idaara-livid.vercel.app/main-abc123.js", bundleContent, "application/javascript");

        return new SplitOriginSyncMocks(
            factory,
            serviceOperationsFactory,
            tokens,
            providerFactory,
            deploymentProvider,
            gitHubService,
            readiness,
            http);
    }

    private static FrontendEnvironmentWiringService CreateSplitOriginService(
        DeployAIDbContext db,
        SplitOriginSyncMocks mocks) =>
        CreateService(
            db,
            mocks.Factory,
            mocks.ServiceOperationsFactory,
            mocks.Tokens,
            mocks.ProviderFactory,
            mocks.GitHubService,
            deploymentReadiness: mocks.Readiness,
            httpClientFactory: new StubHttpClientFactory(mocks.Http));

    private static async Task<Guid> SeedDualTargetProjectAsync(DeployAIDbContext db)
    {
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var vercelCredentialId = Guid.NewGuid();
        var railwayCredentialId = Guid.NewGuid();
        var vercelTargetId = Guid.NewGuid();
        var railwayTargetId = Guid.NewGuid();

        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 2,
            GitHubLogin = "sync-tester",
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
            Name = "iDaara",
            GitHubRepoFullName = "tester/idaara",
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
        await db.SaveChangesAsync();
        return projectId;
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

    private static FrontendEnvironmentWiringService CreateService(
        DeployAIDbContext db,
        Mock<IProviderManagementFactory> managementFactory,
        Mock<IProviderServiceOperationsFactory> serviceOperationsFactory,
        Mock<IProviderCredentialTokenService> tokens,
        Mock<IProviderFactory>? providerFactory = null,
        Mock<IGitHubService>? gitHubService = null,
        Mock<IEncryptionService>? encryption = null,
        Mock<IDeploymentReadinessService>? deploymentReadiness = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        gitHubService ??= new Mock<IGitHubService>();
        gitHubService.Setup(g => g.GetFileMetadataAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubFileMetadata(
                """
                {
                  "rewrites": [
                    { "source": "/api/:path*", "destination": "https://api.example.com/api/:path*" },
                    { "source": "/hubs/:path*", "destination": "https://api.example.com/hubs/:path*" },
                    { "source": "/(.*)", "destination": "/index.html" }
                  ]
                }
                """,
                "sha-existing"));

        return new FrontendEnvironmentWiringService(
            db,
            managementFactory.Object,
            serviceOperationsFactory.Object,
            (providerFactory ?? new Mock<IProviderFactory>()).Object,
            tokens.Object,
            gitHubService.Object,
            (encryption ?? CreateEncryptionMock()).Object,
            httpClientFactory ?? new StubHttpClientFactory(new StubHttpMessageHandler()),
            (deploymentReadiness ?? new Mock<IDeploymentReadinessService>()).Object);
    }

    private static Mock<IEncryptionService> CreateEncryptionMock()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("gh-token");
        return encryption;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses =
            new(StringComparer.OrdinalIgnoreCase);

        public void SetResponse(string url, string content, string mediaType)
        {
            _responses[url] = () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, mediaType)
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_responses.TryGetValue(request.RequestUri!.ToString(), out var factory)
                ? factory()
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                {
                    Content = new StringContent(string.Empty)
                });
    }
}
