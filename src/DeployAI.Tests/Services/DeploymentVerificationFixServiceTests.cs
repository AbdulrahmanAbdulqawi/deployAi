using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class DeploymentVerificationCheckMetadataTests
{
    [Theory]
    [InlineData("failed", "fix_output_directory", true)]
    [InlineData("warning", "reconnect", true)]
    [InlineData("failed", "redeploy_website", false)]
    [InlineData("failed", "redeploy_server", false)]
    [InlineData("passed", "reconnect", false)]
    public void CanRequestClaudeFix_MatchesExpected(string status, string suggestedAction, bool expected) =>
        Assert.Equal(expected, DeploymentVerificationCheckMetadata.CanRequestClaudeFix(status, suggestedAction));

    [Fact]
    public void ResolveReferencedFiles_Homepage404_IncludesVercelAndAngularJson()
    {
        var files = DeploymentVerificationCheckMetadata.ResolveReferencedFiles("website.reachable", "fix_output_directory");
        Assert.Contains("vercel.json", files);
        Assert.Contains("angular.json", files);
    }

    [Fact]
    public void ResolveReferencedFiles_SplitOriginWiring_IncludesAppConfigAndEnvironments()
    {
        var files = DeploymentVerificationCheckMetadata.ResolveReferencedFiles("website.split_origin_wiring", "reconnect");

        Assert.Contains("src/app/app.config.ts", files);
        Assert.Contains("src/environments/environment.production.ts", files);
        Assert.Contains("src/environments/environment.ts", files);
        Assert.Contains("vercel.json", files);
        Assert.Contains("angular.json", files);
    }

    [Fact]
    public void ResolveReferencedFiles_CoolifySplitOriginWiring_OmitsVercelJson()
    {
        var files = DeploymentVerificationCheckMetadata.ResolveReferencedFiles(
            "website.split_origin_wiring",
            "reconnect",
            coolifyFullStack: true);

        Assert.Contains("scripts/write-api-env.mjs", files);
        Assert.Contains("src/app/app.config.ts", files);
        Assert.DoesNotContain(files, file => file.Equals("vercel.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, file => file.Equals("railway.toml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildFixPrompt_IncludesCoolifySplitOriginGuidance_ForCoolifyProvider()
    {
        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            "Build failed",
            "Missing apiBaseUrl in production bundle",
            ["client/src/app/app.config.ts"],
            true);

        var prompt = ClaudeDeploymentPrompts.BuildFixPrompt(
            "owner",
            "repo",
            "main",
            ProviderNameValues.Coolify,
            "Angular",
            analysis);

        Assert.Contains("Coolify full-stack", prompt);
        Assert.Contains("Do not add `vercel.json` or `railway.toml`", prompt);
        Assert.DoesNotContain("*.vercel.app/api/*", prompt);
    }

    [Fact]
    public void BuildVerificationFixPrompt_IncludesCoolifyHints_ForCoolifyProvider()
    {
        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            "Split-origin wiring failed",
            "Health check failed",
            ["client/src/app/app.config.ts"],
            true);

        var verificationContext = new DeploymentVerificationFixContext(
            "website.reachable",
            "website",
            "Website homepage",
            "Website returned 404",
            "https://app.example.com/",
            "https://app.example.com/",
            "https://api.example.com/");

        var prompt = ClaudeDeploymentPrompts.BuildVerificationFixPrompt(
            "owner",
            "repo",
            "main",
            ProviderNameValues.Coolify,
            "angular",
            analysis,
            verificationContext,
            new DeployTargetConfig { Role = "website", Framework = "angular", RootDirectory = "client" });

        Assert.Contains("Coolify website app", prompt);
        Assert.Contains("write-api-env.mjs", prompt);
        Assert.DoesNotContain("Set `vercel.json` `outputDirectory`", prompt);
    }

    [Theory]
    [InlineData("warning", "reconnect", true)]
    [InlineData("warning", "redeploy_website", false)]
    public void CanRequestClaudeFix_SplitOriginWiringWarning_MatchesExpected(
        string status,
        string suggestedAction,
        bool expected) =>
        Assert.Equal(expected, DeploymentVerificationCheckMetadata.CanRequestClaudeFix(status, suggestedAction));
}

public class DeploymentVerificationFixServiceTests
{
    [Fact]
    public async Task GenerateVerificationFix_RejectsRedeployChecks()
    {
        await using var db = CreateDb();
        var (userId, deploymentId, _) = await SeedSuccessfulDeploymentAsync(db);

        var verification = new Mock<IDeploymentVerificationService>();
        verification
            .Setup(v => v.VerifyAsync(deploymentId, DeploymentVerificationScope.Both, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentVerificationResult(
                false,
                "both",
                [
                    new DeploymentVerificationCheck(
                        "server.health",
                        "server",
                        "API health",
                        "failed",
                        "Health check failed",
                        "https://api.example.com/api/v1/health",
                        "redeploy_server",
                        false,
                        ["Program.cs"])
                ],
                DateTimeOffset.UtcNow));

        var service = CreateService(db, verification.Object);

        var ex = await Assert.ThrowsAsync<DeployAI.Core.Exceptions.DeployAIException>(() =>
            service.GenerateVerificationFixPullRequestAsync(
                userId,
                deploymentId,
                "server.health",
                null,
                null,
                CancellationToken.None));

        Assert.Equal("fix_not_applicable", ex.ErrorCode);
    }

    [Fact]
    public async Task GenerateVerificationFix_AllowsHomepage404Check()
    {
        await using var db = CreateDb();
        var (userId, deploymentId, deploymentTargetId) = await SeedSuccessfulDeploymentAsync(db, includeTargetIds: true);

        var verification = new Mock<IDeploymentVerificationService>();
        verification
            .Setup(v => v.VerifyAsync(deploymentId, DeploymentVerificationScope.Both, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentVerificationResult(
                false,
                "both",
                [
                    new DeploymentVerificationCheck(
                        "website.reachable",
                        "website",
                        "Website homepage",
                        "failed",
                        "Website returned 404",
                        "https://app.vercel.app/",
                        "fix_output_directory",
                        true,
                        ["vercel.json", "angular.json"])
                ],
                DateTimeOffset.UtcNow));

        var generator = new Mock<IDeploymentFixGenerator>();
        generator
            .Setup(g => g.GenerateFixFilesAsync(
                It.IsAny<Guid>(),
                "owner",
                "repo",
                It.IsAny<string>(),
                "token",
                "vercel",
                "angular",
                It.IsAny<DeployTargetConfig>(),
                It.IsAny<DeploymentFailureAnalysis>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DeploymentVerificationFixContext>()))
            .ReturnsAsync([new GeneratedDeploymentFile("vercel.json", "{}")]);

        var gitHub = CreateGitHubMock();
        var service = CreateService(db, verification.Object, generator.Object, gitHub.Object);

        var result = await service.GenerateVerificationFixPullRequestAsync(
            userId,
            deploymentId,
            "website.reachable",
            deploymentTargetId,
            null,
            CancellationToken.None);

        Assert.Single(result.CommittedFiles);
        Assert.Contains("vercel.json", result.CommittedFiles);
    }

    [Fact]
    public async Task GenerateVerificationFix_AllowsSplitOriginWiringWarning()
    {
        await using var db = CreateDb();
        var (userId, deploymentId, deploymentTargetId) = await SeedSuccessfulDeploymentAsync(db, includeTargetIds: true);

        var verification = new Mock<IDeploymentVerificationService>();
        verification
            .Setup(v => v.VerifyAsync(deploymentId, DeploymentVerificationScope.Both, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentVerificationResult(
                false,
                "website",
                [
                    new DeploymentVerificationCheck(
                        "website.split_origin_wiring",
                        "website",
                        "Split-origin bundle wiring",
                        "warning",
                        "Deployed SPA bundle is missing split-origin wiring (apiBaseInterceptor and apiBaseUrl). A production redeploy may be required.",
                        "https://tickethub-sooty-five.vercel.app/",
                        "reconnect",
                        true,
                        [
                            "vercel.json",
                            "angular.json",
                            "src/app/app.config.ts",
                            "src/environments/environment.production.ts",
                            "src/environments/environment.ts"
                        ])
                ],
                DateTimeOffset.UtcNow));

        var generator = new Mock<IDeploymentFixGenerator>();
        generator
            .Setup(g => g.GenerateFixFilesAsync(
                It.IsAny<Guid>(),
                "owner",
                "repo",
                It.IsAny<string>(),
                "token",
                "vercel",
                "angular",
                It.IsAny<DeployTargetConfig>(),
                It.IsAny<DeploymentFailureAnalysis>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DeploymentVerificationFixContext>()))
            .ReturnsAsync([new GeneratedDeploymentFile("src/app/app.config.ts", "patched")]);

        var gitHub = CreateGitHubMock();
        var service = CreateService(db, verification.Object, generator.Object, gitHub.Object);

        var result = await service.GenerateVerificationFixPullRequestAsync(
            userId,
            deploymentId,
            "website.split_origin_wiring",
            deploymentTargetId,
            null,
            CancellationToken.None);

        Assert.Single(result.CommittedFiles);
        Assert.Contains("src/app/app.config.ts", result.CommittedFiles);
    }

    private static DeploymentFixService CreateService(
        DeployAI.Data.DeployAIDbContext db,
        IDeploymentVerificationService verification,
        IDeploymentFixGenerator? generator = null,
        DeployAI.Infrastructure.GitHub.IGitHubService? gitHub = null)
    {
        generator ??= new Mock<IDeploymentFixGenerator>().Object;
        gitHub ??= CreateGitHubMock().Object;
        var failureAnalyzer = new Mock<IDeploymentFailureAnalyzer>().Object;
        var encryption = new Mock<DeployAI.Core.Security.IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("token");
        return new DeploymentFixService(db, gitHub, generator, failureAnalyzer, verification, encryption.Object);
    }

    private static Mock<DeployAI.Infrastructure.GitHub.IGitHubService> CreateGitHubMock()
    {
        var gitHub = new Mock<DeployAI.Infrastructure.GitHub.IGitHubService>();
        gitHub
            .Setup(s => s.GetBranchHeadShaAsync("token", "owner", "repo", "main", It.IsAny<CancellationToken>()))
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
                "main",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeployAI.Infrastructure.GitHub.GitHubPullRequestResponse(9, "https://github.com/owner/repo/pull/9"));
        return gitHub;
    }

    private static async Task<(Guid UserId, Guid DeploymentId, Guid DeploymentTargetId)> SeedSuccessfulDeploymentAsync(
        DeployAI.Data.DeployAIDbContext db,
        bool includeTargetIds = true)
    {
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var deployTargetId = Guid.NewGuid();
        var deploymentTargetId = Guid.NewGuid();

        db.Users.Add(new DeployAI.Data.Entities.User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GitHubTokenEncrypted = [1]
        });

        db.Projects.Add(new DeployAI.Data.Entities.Project
        {
            Id = projectId,
            UserId = userId,
            Name = "Demo",
            GitHubRepoFullName = "owner/repo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var deployTarget = new DeployAI.Data.Entities.DeployTarget
        {
            Id = deployTargetId,
            ProjectId = projectId,
            ProviderName = "vercel",
            CredentialId = Guid.NewGuid(),
            ProviderProjectId = "demo",
            ConfigJson = """{"role":"website","framework":"angular","outputDirectory":"dist/app/browser"}""",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.DeployTargets.Add(deployTarget);

        db.Deployments.Add(new DeployAI.Data.Entities.Deployment
        {
            Id = deploymentId,
            ProjectId = projectId,
            Branch = "main",
            Status = DeploymentStatuses.Success,
            CreatedAt = DateTimeOffset.UtcNow,
            GitCommitSha = "abc123"
        });

        db.DeploymentTargets.Add(new DeployAI.Data.Entities.DeploymentTarget
        {
            Id = deploymentTargetId,
            DeploymentId = deploymentId,
            DeployTargetId = deployTargetId,
            ProviderName = "vercel",
            Status = DeploymentStatuses.Success,
            DeployUrl = "https://app.vercel.app",
            DeployTarget = deployTarget
        });

        await db.SaveChangesAsync();
        return (userId, deploymentId, deploymentTargetId);
    }

    private static DeployAI.Data.DeployAIDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeployAIDbContext(options);
    }
}
