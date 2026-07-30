using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RichardSzalay.MockHttp;

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

    [Fact]
    public async Task EnsureAsync_PutsTheDockerfilePathOnTheTargetTheDeployGoesOnToRead()
    {
        // The bug this exists for, in one assertion. EnsureAsync generated the Dockerfile, committed
        // it, told Coolify to build with it, and wrote the choice to the database row -- but not to
        // the entity the rest of the deploy holds. The caller re-parses that entity to build the
        // configuration it pushes to Coolify moments later, saw no Dockerfile path, resolved the
        // build pack to Nixpacks, and overwrote the switch. Mirqab's Angular 20 site then built on
        // Nixpacks' default Node 18 and failed the version check, with a correct node:22 Dockerfile
        // sitting unused in the repository.
        var (harness, project, target) = CreateSwitchable();

        await harness.Provisioner.EnsureAsync(project, target, "main", CancellationToken.None);

        Assert.Equal("/Dockerfile", DeployTargetConfig.Parse(target.ConfigJson).DockerfilePath);
    }

    [Fact]
    public async Task EnsureAsync_LeavesThePreDeployConfigSyncPushingTheDockerfileBuildPack()
    {
        // The same fact stated where it actually bit. Asserting the field alone would still pass if
        // the sync ignored it, and the sync is what reached Coolify last and decided the build.
        var (harness, project, target) = CreateSwitchable();

        await harness.Provisioner.EnsureAsync(project, target, "main", CancellationToken.None);

        var config = DeployTargetConfig.Parse(target.ConfigJson);
        await harness.Provider.UpdateApplicationConfigAsync(
            new ProviderCredentials(CoolifyCredentialStorage.Serialize(CoolifyUrl, "coolify-token")),
            "app-uuid",
            new UpdateProviderApplicationConfigRequest(
                config.Framework,
                config.RootDirectory,
                config.OutputDirectory,
                config.BuildCommand,
                config.InstallCommand,
                config.StartCommand,
                config.DockerfilePath),
            CancellationToken.None);

        Assert.Contains("\"build_pack\":\"dockerfile\"", harness.LastConfigSyncBody);
    }

    [Fact]
    public async Task EnsureAsync_LeavesThePreDeployConfigSyncPushingThePortTheImageListensOn()
    {
        // The second half of the same failure, one layer along. ConfigureDockerfileBuildAsync sets
        // ports_exposes from the generated EXPOSE, and the sync then re-guessed it from the
        // framework name -- answering 8080 for any Dockerfile build. Mirqab's static Angular image
        // serves on 3000, so the proxy was pointed at a closed port: container healthy, deploy
        // green, every request a 502 from Traefik.
        var (harness, project, target) = CreateSwitchable();

        await harness.Provisioner.EnsureAsync(project, target, "main", CancellationToken.None);

        var config = DeployTargetConfig.Parse(target.ConfigJson);
        await harness.Provider.UpdateApplicationConfigAsync(
            new ProviderCredentials(CoolifyCredentialStorage.Serialize(CoolifyUrl, "coolify-token")),
            "app-uuid",
            new UpdateProviderApplicationConfigRequest(
                config.Framework,
                config.RootDirectory,
                config.OutputDirectory,
                config.BuildCommand,
                config.InstallCommand,
                config.StartCommand,
                config.DockerfilePath,
                CoolifyBuildPack: null,
                ExposedPort: config.ExposedPort),
            CancellationToken.None);

        Assert.Contains("\"ports_exposes\":\"3000\"", harness.LastConfigSyncBody);
    }

    [Fact]
    public async Task EnsureAsync_TellsCoolifyWhichNodeToUseIfItFallsBackToNixpacks()
    {
        // Mirqab's package.json declares no engines, so Nixpacks picked its default of Node 18 and
        // Angular 20 refused to build on it. Coolify prints the remedy — set NIXPACKS_NODE_VERSION —
        // and waits for a human; the same value is already resolved when generating the Dockerfile,
        // so there is nothing to wait for.
        var (harness, project, target) = CreateSwitchable();

        await harness.Provisioner.EnsureAsync(project, target, "main", CancellationToken.None);

        Assert.Contains("NIXPACKS_NODE_VERSION", harness.EnvVarBodies);
        Assert.Contains("\"22\"", harness.EnvVarBodies);
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
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string _, string _, string? _,
                       IReadOnlyList<string> keys, string? _, string? _, string? _, string? _, CancellationToken _) =>
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

    /// <summary>
    /// A provisioner that gets all the way through: a generator that produces a Dockerfile, a
    /// Coolify that accepts the build-pack switch, and a real relational database so the
    /// <c>UPDATE deploy_targets</c> actually executes.
    /// </summary>
    /// <remarks>
    /// SQLite rather than the in-memory provider on purpose. Every service here persists a target's
    /// configuration with <c>ExecuteSqlInterpolatedAsync</c>, which the in-memory provider refuses —
    /// so the write, and everything after it, has never been reachable from a test. That is why a
    /// missing in-memory assignment beside one of those writes survived to production.
    /// </remarks>
    private static (SsrWebsiteBuildProvisionerHarness, Project, DeployTarget) CreateSwitchable()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseSqlite(connection)
            .Options);
        db.Database.EnsureCreated();

        db.Users.Add(new User
        {
            Id = UserId,
            GitHubId = 1,
            GitHubLogin = "tester",
            GitHubTokenEncrypted = [1],
            CreatedAt = DateTimeOffset.UtcNow
        });

        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = "Mirqab",
            GitHubRepoFullName = "tester/Mirqab",
            DefaultBranch = "master",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var credential = new ProviderCredential
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            ProviderName = "coolify",
            Kind = CredentialKind.Deployment,
            TokenEncrypted = [1],
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ProviderCredentials.Add(credential);

        // Mirqab's real website target: Angular with a build command, which is what makes the
        // fallback resolve to Nixpacks rather than static.
        var target = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            CredentialId = credential.Id,
            ProviderName = "coolify",
            ProviderProjectId = "app-uuid",
            ConfigJson = """
                {"role":"website","framework":"angular","rootDirectory":"client",
                 "outputDirectory":"dist/client/browser","buildCommand":"npm run build"}
                """,
            CreatedAt = DateTimeOffset.UtcNow
        };
        project.DeployTargets.Add(target);
        db.Projects.Add(project);
        db.SaveChanges();

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("{}");

        var harness = new SsrWebsiteBuildProvisionerHarness();
        var dockerfiles = new Mock<IServerDockerfileProvisioner>();
        dockerfiles.Setup(d => d.EnsureSsrWebsiteDockerfileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => harness.Called = true)
            // NodeMajor is what the real generator resolves from package.json (engines, else its
            // default) -- Mirqab declares no engines, so 22.
            .ReturnsAsync(new ServerDockerfileResult("client", "/Dockerfile", 3000, NodeMajor: 22));

        var coolify = new MockHttpMessageHandler();
        coolify.When(HttpMethod.Get, $"{CoolifyUrl}/api/v1/applications/app-uuid/envs")
            .Respond(System.Net.HttpStatusCode.OK, "application/json", "[]");
        coolify.When(HttpMethod.Patch, $"{CoolifyUrl}/api/v1/applications/app-uuid/envs/bulk")
            .Respond(async request =>
            {
                harness.EnvVarBodies += request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
            });
        coolify.When(HttpMethod.Patch, $"{CoolifyUrl}/api/v1/applications/app-uuid")
            .Respond(async request =>
            {
                harness.LastConfigSyncBody = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
            });

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CoolifyCredentialStorage.Serialize(CoolifyUrl, "coolify-token"));

        harness.Provider = new DeployAI.Providers.Coolify.CoolifyProvider(coolify.ToHttpClient());
        harness.Provisioner = new SsrWebsiteBuildProvisioner(
            db,
            dockerfiles.Object,
            harness.Provider,
            tokens.Object,
            encryption.Object,
            NullLogger<SsrWebsiteBuildProvisioner>.Instance);

        return (harness, project, target);
    }

    private const string CoolifyUrl = "https://coolify.example.com";

    private sealed class SsrWebsiteBuildProvisionerHarness
    {
        public SsrWebsiteBuildProvisioner Provisioner { get; set; } = null!;
        public bool Called { get; set; }
        public IReadOnlyList<string> BuildTimeEnvKeys { get; set; } = [];
        public DeployAI.Providers.Coolify.CoolifyProvider Provider { get; set; } = null!;
        public string LastConfigSyncBody { get; set; } = string.Empty;
        public string EnvVarBodies { get; set; } = string.Empty;
    }
}
