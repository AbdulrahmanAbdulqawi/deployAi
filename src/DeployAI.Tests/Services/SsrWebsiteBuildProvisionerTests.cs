using DeployAI.Api.Services;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// The generated Dockerfile can only inline a value the build receives as a build arg, and it only
/// declares an ARG for a key it was told about. These cover where that list comes from.
/// </summary>
public class SsrWebsiteBuildProvisionerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task EnsureAsync_DeclaresTheApiUrlKey_EvenWhenTheProjectStoresNoEnvironmentVariables()
    {
        // The whole reason this generator exists is to stop a Next.js build baking in its localhost
        // fallback for the API. But the API URL is *derived* by DeployAI after the server deploys --
        // it is never typed into the managed-environment screen, so it is never in the project's
        // stored variables, which used to be the only source the key list was read from. The one
        // variable the feature exists for was structurally invisible to it, and the site shipped
        // calling localhost.
        var (provisioner, project, target) = Create(storedKeys: null, framework: "next");

        var keys = await CaptureBuildTimeEnvKeys(provisioner, project, target);

        Assert.Contains("NEXT_PUBLIC_API_URL", keys);
    }

    [Fact]
    public async Task EnsureAsync_KeepsTheProjectsOwnKeysAlongsideTheDerivedOne()
    {
        // A union, not a replacement: a public value the user set themselves has to reach the build
        // too, and the derived key must not evict it.
        var (provisioner, project, target) = Create(
            storedKeys: ["NEXT_PUBLIC_SITE_NAME", "DATABASE_URL"],
            framework: "next");

        var keys = await CaptureBuildTimeEnvKeys(provisioner, project, target);

        Assert.Contains("NEXT_PUBLIC_API_URL", keys);
        Assert.Contains("NEXT_PUBLIC_SITE_NAME", keys);
        // Passed through as a key; the generator itself drops non-public ones so no secret can
        // become a build arg. Filtering twice would just hide which layer is responsible.
        Assert.Contains("DATABASE_URL", keys);
    }

    [Fact]
    public async Task EnsureAsync_UsesTheKeyConventionOfTheFrameworkBeingBuilt()
    {
        // Each framework inlines its own prefix. Emitting Next's key for a Nuxt build would declare
        // an ARG nothing reads and leave the one that matters undeclared.
        var (provisioner, project, target) = Create(storedKeys: null, framework: "nuxt");

        var keys = await CaptureBuildTimeEnvKeys(provisioner, project, target);

        Assert.Contains("NUXT_PUBLIC_API_URL", keys);
        Assert.DoesNotContain("NEXT_PUBLIC_API_URL", keys);
    }

    /// <summary>
    /// Runs the provisioner far enough to see what it hands the generator. The generator mock
    /// returns null, which makes <c>EnsureAsync</c> stop before it needs a provider token or an
    /// HTTP call — the key list is already decided by then.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CaptureBuildTimeEnvKeys(
        SsrWebsiteBuildProvisionerHarness harness,
        Project project,
        DeployTarget target)
    {
        await harness.Provisioner.EnsureAsync(project, target, "main", CancellationToken.None);
        Assert.True(harness.Called, "The Dockerfile generator was never reached.");
        return harness.BuildTimeEnvKeys;
    }

    private static (SsrWebsiteBuildProvisionerHarness, Project, DeployTarget) Create(
        string[]? storedKeys,
        string framework)
    {
        var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        db.Users.Add(new User
        {
            Id = UserId,
            GitHubId = 1,
            GitHubLogin = "tester",
            GitHubTokenEncrypted = [1],
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = "yemenConnect",
            GitHubRepoFullName = "tester/yemenConnect",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            EnvironmentVariablesEncrypted = storedKeys is { Length: > 0 } ? [1] : null
        };

        var target = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ProviderName = "coolify",
            ProviderProjectId = "app-uuid",
            ConfigJson = $$"""{"role":"website","framework":"{{framework}}","serviceDirectory":"apps/web"}""",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>()))
            .Returns(System.Text.Json.JsonSerializer.Serialize(
                (storedKeys ?? []).ToDictionary(k => k, _ => new { Value = "set", IsSecret = false })));

        var harness = new SsrWebsiteBuildProvisionerHarness();
        var dockerfiles = new Mock<IServerDockerfileProvisioner>();
        dockerfiles.Setup(d => d.EnsureSsrWebsiteDockerfileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string _, string _, string? _,
                       IReadOnlyList<string> keys, string? _, string? _, string? _, CancellationToken _) =>
            {
                harness.Called = true;
                harness.BuildTimeEnvKeys = keys;
            })
            .ReturnsAsync((ServerDockerfileResult?)null);

        harness.Provisioner = new SsrWebsiteBuildProvisioner(
            db,
            dockerfiles.Object,
            new DeployAI.Providers.Coolify.CoolifyProvider(new HttpClient()),
            Mock.Of<IProviderCredentialTokenService>(),
            encryption.Object,
            NullLogger<SsrWebsiteBuildProvisioner>.Instance);

        return (harness, project, target);
    }

    private sealed class SsrWebsiteBuildProvisionerHarness
    {
        public SsrWebsiteBuildProvisioner Provisioner { get; set; } = null!;
        public bool Called { get; set; }
        public IReadOnlyList<string> BuildTimeEnvKeys { get; set; } = [];
    }
}
