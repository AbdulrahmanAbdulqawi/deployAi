using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class DeploymentSetupServiceTests
{
    [Fact]
    public async Task GenerateSetupBranchAsync_UsesRepositoryDefaultBranch_ForPullRequestBase()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GitHubTokenEncrypted = [1]
        });
        await db.SaveChangesAsync();

        var readiness = new Mock<IDeploymentReadinessService>();
        readiness
            .Setup(service => service.ScanRepositoryAsync(
                It.IsAny<string>(),
                "owner",
                "repo",
                "feature/x",
                It.IsAny<IReadOnlyList<DeploymentPlanPart>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentReadinessResult(
                false,
                "sha123",
                true,
                [new MissingDeploymentFile("vercel.json", "missing", DeploymentFileSeverity.Blocking)],
                []));

        var generator = new Mock<IDeploymentFileGenerator>();
        generator
            .Setup(g => g.GenerateMissingFilesAsync(
                "owner",
                "repo",
                "feature/x",
                "token",
                It.IsAny<IReadOnlyList<DeploymentPlanPart>>(),
                It.IsAny<IReadOnlyList<MissingDeploymentFile>>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GeneratedDeploymentFile("vercel.json", "{}")]);

        var gitHub = new Mock<IGitHubService>();
        gitHub
            .Setup(service => service.CreateBranchAsync(It.IsAny<string>(), "owner", "repo", It.IsAny<string>(), "sha123", It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha123");
        gitHub
            .Setup(service => service.CommitFilesAsync(It.IsAny<string>(), "owner", "repo", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("commit456");
        gitHub
            .Setup(service => service.GetRepositoryAsync(It.IsAny<string>(), "owner", "repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepo("owner/repo", "develop", false));
        gitHub
            .Setup(service => service.CreatePullRequestAsync(
                It.IsAny<string>(),
                "owner",
                "repo",
                It.IsAny<string>(),
                It.IsAny<string>(),
                "develop",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResponse(42, "https://github.com/owner/repo/pull/42"));

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        var environmentWiring = new Mock<IFrontendEnvironmentWiringService>();

        var service = new DeploymentSetupService(
            db,
            gitHub.Object,
            readiness.Object,
            SelectorReturning(generator.Object),
            environmentWiring.Object,
            encryption.Object,
            new Mock<IProjectBranchDeployService>().Object);

        var parts = new[]
        {
            new DeploymentPlanPart("website", "vercel", "client", null, null, null, null, null, "angular", null, null),
            new DeploymentPlanPart("server", "railway", "api", "api", null, null, null, null, "dotnet", null, null)
        };

        var result = await service.GenerateSetupBranchAsync(
            userId,
            "owner",
            "repo",
            new DeploymentSetupRequest("feature/x", parts),
            null,
            CancellationToken.None);

        Assert.Equal(42, result.PullRequestNumber);
        gitHub.Verify(service => service.CreatePullRequestAsync(
            "token",
            "owner",
            "repo",
            It.IsAny<string>(),
            It.IsAny<string>(),
            "develop",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateSetupBranchAsync_UsesCoolifyPullRequestMessaging_ForCoolifyFullStack()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GitHubTokenEncrypted = [1]
        });
        await db.SaveChangesAsync();

        var readiness = new Mock<IDeploymentReadinessService>();
        readiness
            .Setup(service => service.ScanRepositoryAsync(
                It.IsAny<string>(),
                "owner",
                "repo",
                "main",
                It.IsAny<IReadOnlyList<DeploymentPlanPart>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentReadinessResult(
                false,
                "sha123",
                true,
                [new MissingDeploymentFile("client/scripts/write-api-env.mjs", "missing", DeploymentFileSeverity.Blocking)],
                []));

        var generator = new Mock<IDeploymentFileGenerator>();
        generator
            .Setup(g => g.GenerateMissingFilesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<DeploymentPlanPart>>(),
                It.IsAny<IReadOnlyList<MissingDeploymentFile>>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GeneratedDeploymentFile("client/scripts/write-api-env.mjs", "// coolify")]);

        string? capturedCommitMessage = null;
        string? capturedPrTitle = null;
        string? capturedPrBody = null;

        var gitHub = new Mock<IGitHubService>();
        gitHub.Setup(service => service.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha123");
        gitHub.Setup(service => service.CommitFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, IReadOnlyList<(string, string)>, CancellationToken>(
                (_, _, _, _, message, _, _) => capturedCommitMessage = message)
            .ReturnsAsync("commit456");
        gitHub.Setup(service => service.GetRepositoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepo("owner/repo", "main", false));
        gitHub.Setup(service => service.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, string, string, CancellationToken>(
                (_, _, _, title, _, _, body, _) =>
                {
                    capturedPrTitle = title;
                    capturedPrBody = body;
                })
            .ReturnsAsync(new GitHubPullRequestResponse(9, "https://example.com/pr/9"));

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        var service = new DeploymentSetupService(
            db,
            gitHub.Object,
            readiness.Object,
            SelectorReturning(generator.Object),
            new Mock<IFrontendEnvironmentWiringService>().Object,
            encryption.Object,
            new Mock<IProjectBranchDeployService>().Object);

        var parts = new[]
        {
            new DeploymentPlanPart("website", "coolify", "client", null, null, null, null, null, "angular", null, null),
            new DeploymentPlanPart("server", "coolify", "src/Api", "src/Api", null, null, null, null, "dotnet", null, null)
        };

        await service.GenerateSetupBranchAsync(
            userId,
            "owner",
            "repo",
            new DeploymentSetupRequest("main", parts),
            null,
            CancellationToken.None);

        Assert.Equal("DeployAI: add Coolify full-stack deployment files", capturedCommitMessage);
        Assert.Equal("DeployAI: Coolify full-stack deployment setup", capturedPrTitle);
        Assert.Contains("FRONTEND_URL", capturedPrBody);
        Assert.DoesNotContain("vercel.json", capturedPrBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateSetupBranchAsync_IncludesRecommendedMissingFiles()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GitHubTokenEncrypted = [1]
        });
        await db.SaveChangesAsync();

        var readiness = new Mock<IDeploymentReadinessService>();
        readiness
            .Setup(service => service.ScanRepositoryAsync(
                It.IsAny<string>(),
                "owner",
                "repo",
                "main",
                It.IsAny<IReadOnlyList<DeploymentPlanPart>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentReadinessResult(
                true,
                "sha123",
                true,
                [new MissingDeploymentFile("Program.cs", "CORS", DeploymentFileSeverity.Recommended)],
                []));

        IReadOnlyList<MissingDeploymentFile>? capturedMissing = null;
        var generator = new Mock<IDeploymentFileGenerator>();
        generator
            .Setup(g => g.GenerateMissingFilesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<DeploymentPlanPart>>(),
                It.IsAny<IReadOnlyList<MissingDeploymentFile>>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, IReadOnlyList<DeploymentPlanPart>, IReadOnlyList<MissingDeploymentFile>, Func<string, Task>?, CancellationToken>(
                (_, _, _, _, _, missing, _, _) => capturedMissing = missing)
            .ReturnsAsync([new GeneratedDeploymentFile("Program.cs", "// patched")]);

        var gitHub = new Mock<IGitHubService>();
        gitHub.Setup(service => service.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha123");
        gitHub.Setup(service => service.CommitFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("commit456");
        gitHub.Setup(service => service.GetRepositoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepo("owner/repo", "main", false));
        gitHub.Setup(service => service.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResponse(1, "https://example.com/pr/1"));

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        var service = new DeploymentSetupService(
            db,
            gitHub.Object,
            readiness.Object,
            SelectorReturning(generator.Object),
            new Mock<IFrontendEnvironmentWiringService>().Object,
            encryption.Object,
            new Mock<IProjectBranchDeployService>().Object);

        var parts = new[]
        {
            new DeploymentPlanPart("website", "vercel", "client", null, null, null, null, null, "angular", null, null),
            new DeploymentPlanPart("server", "railway", "api", "api", null, null, null, null, "dotnet", null, null)
        };

        await service.GenerateSetupBranchAsync(userId, "owner", "repo", new DeploymentSetupRequest("main", parts), null, CancellationToken.None);

        Assert.NotNull(capturedMissing);
        Assert.Contains(capturedMissing!, file => file.Severity == DeploymentFileSeverity.Recommended);
    }

    [Fact]
    public async Task MergeSetupPullRequestAsync_SyncsEnvironment_WhenProjectIdProvided()
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
        gitHub.Setup(service => service.MergePullRequestAsync(It.IsAny<string>(), "owner", "repo", 7, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var environmentWiring = new Mock<IFrontendEnvironmentWiringService>();
        environmentWiring
            .Setup(service => service.SyncCrossProviderEnvironmentAsync(
                projectId,
                It.IsAny<EnvironmentSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnvironmentSyncResult(
                Success: true,
                DriftDetected: false,
                Skipped: false,
                SkipReason: null,
                ResolvedWebsiteUrl: "https://app.vercel.app",
                ResolvedApiUrl: "https://api.railway.app",
                RailwayKeysApplied: ["AllowedOrigins__0"],
                VercelKeysApplied: ["IDAARA_API_URL"],
                VerificationMessages: [],
                DriftDetails: [],
                Source: "deployment-setup",
                CompletedAt: DateTimeOffset.UtcNow));

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        var service = new DeploymentSetupService(
            db,
            gitHub.Object,
            new Mock<IDeploymentReadinessService>().Object,
            SelectorReturning(new Mock<IDeploymentFileGenerator>().Object),
            environmentWiring.Object,
            encryption.Object,
            new Mock<IProjectBranchDeployService>().Object);

        var result = await service.MergeSetupPullRequestAsync(
            userId,
            "owner",
            "repo",
            7,
            projectId,
            CancellationToken.None);

        Assert.True(result.Merged);
        Assert.Equal("completed", result.EnvSyncStatus);
        Assert.Contains("AllowedOrigins__0", result.RailwayKeysApplied);
        Assert.Contains("IDAARA_API_URL", result.VercelKeysApplied);
    }

    [Fact]
    public async Task GenerateSetupBranchAsync_ForceRegenerate_UsesFullChecklistWhenScanIsClean()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GitHubTokenEncrypted = [1]
        });
        await db.SaveChangesAsync();

        var parts = new[]
        {
            new DeploymentPlanPart("website", "vercel", "client", null, null, null, null, null, "angular", null, null),
            new DeploymentPlanPart("server", "railway", "api", "api", null, null, null, null, "dotnet", null, null)
        };

        var readiness = new Mock<IDeploymentReadinessService>();
        readiness
            .Setup(service => service.ScanRepositoryAsync(
                It.IsAny<string>(),
                "owner",
                "repo",
                "main",
                parts,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentReadinessResult(true, "sha123", true, [], []));

        IReadOnlyList<MissingDeploymentFile>? capturedMissing = null;
        var generator = new Mock<IDeploymentFileGenerator>();
        generator
            .Setup(g => g.GenerateMissingFilesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                parts,
                It.IsAny<IReadOnlyList<MissingDeploymentFile>>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, IReadOnlyList<DeploymentPlanPart>, IReadOnlyList<MissingDeploymentFile>, Func<string, Task>?, CancellationToken>(
                (_, _, _, _, _, missing, _, _) => capturedMissing = missing)
            .ReturnsAsync([new GeneratedDeploymentFile("client/vercel.json", "{}")]);

        var gitHub = new Mock<IGitHubService>();
        gitHub.Setup(service => service.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha123");
        gitHub.Setup(service => service.CommitFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("commit456");
        gitHub.Setup(service => service.GetRepositoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepo("owner/repo", "main", false));
        gitHub.Setup(service => service.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResponse(1, "https://example.com/pr/1"));

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");

        var service = new DeploymentSetupService(
            db,
            gitHub.Object,
            readiness.Object,
            SelectorReturning(generator.Object),
            new Mock<IFrontendEnvironmentWiringService>().Object,
            encryption.Object,
            new Mock<IProjectBranchDeployService>().Object);

        await service.GenerateSetupBranchAsync(
            userId,
            "owner",
            "repo",
            new DeploymentSetupRequest("main", parts, ForceRegenerate: true),
            null,
            CancellationToken.None);

        Assert.NotNull(capturedMissing);
        Assert.Contains(capturedMissing!, file => file.Path.Contains("vercel.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedMissing!, file => file.Path.Contains("Program.cs", StringComparison.OrdinalIgnoreCase));
    }

    private static IDeploymentFileGeneratorSelector SelectorReturning(
        IDeploymentFileGenerator generator,
        string mode = DeploymentFileGeneratorSelector.AiMode)
    {
        var selector = new Mock<IDeploymentFileGeneratorSelector>();
        selector
            .Setup(s => s.SelectAsync(It.IsAny<bool?>(), It.IsAny<Func<string, Task>?>()))
            .ReturnsAsync(new DeploymentFileGeneratorSelection(generator, mode));
        return selector.Object;
    }

    private static DeployAIDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeployAIDbContext(options);
    }
}
