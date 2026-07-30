using DeployAI.Api.Services;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

public class RailwayDatabaseProvisioningServiceTests
{
    private const string Owner = "tester";
    private const string Repo = "yemenConnect";
    private const string Branch = "main";

    private const string WebCsproj = """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""";

    [Fact]
    public async Task DetectRequirements_ReadsTheAppsettingsInsideTheNestedProject()
    {
        // The layout this service used to walk to by hand: the service directory is the build
        // context and holds project directories, and the connection strings live one level down.
        // Behaviour must be identical now that the walk is the shared resolver's.
        var github = Monorepo("""{ "ConnectionStrings": { "Postgres": "Host=localhost;Database=yemenhub" } }""");

        var profile = await Detect(github, serviceDirectory: "backend/src");

        Assert.True(profile.RequiresPostgres);
        Assert.Contains("Postgres", profile.ConnectionStringKeys);
        Assert.False(profile.IsInconclusive);
    }

    [Fact]
    public async Task DetectRequirements_ReadsAPrismaSchemaBesideTheApp()
    {
        // New here. Classification already looked at prisma/schema.prisma, but the deploy path did
        // not, so a Node app whose only evidence is its schema was promised Postgres at the wizard
        // and then deployed without one.
        var github = new Mock<IGitHubService>();
        StubDir(github, "apps/api", File("apps/api/package.json"));
        StubFile(github, "apps/api/package.json", """{ "name": "api" }""");
        StubFile(github, "apps/api/prisma/schema.prisma",
            """datasource db { provider = "postgresql" url = env("DATABASE_URL") }""");

        var profile = await Detect(github, serviceDirectory: "apps/api");

        Assert.True(profile.RequiresPostgres);
        Assert.False(profile.IsInconclusive);
    }

    [Fact]
    public async Task DetectRequirements_ReadsACompose_FileBesideTheAppNotOnlyAtTheRepoRoot()
    {
        // Root-only was the old rule, so a monorepo that keeps its compose file with its backend
        // was read as needing nothing.
        var github = Monorepo(appsettings: null);
        StubFile(github, "backend/src/YemenHub.Api/docker-compose.yml",
            """
            services:
              cache:
                image: redis:7
            """);

        var profile = await Detect(github, serviceDirectory: "backend/src");

        Assert.True(profile.RequiresRedis);
    }

    [Fact]
    public async Task DetectRequirements_SaysItCouldNotLook_RatherThanThatNoDatabaseIsNeeded()
    {
        // The distinction the whole migration is for. An app that needs Postgres and gets none
        // starts cleanly and fails at its first query, which looks nothing like a scan that could
        // not run — so the two must never produce the same profile.
        var github = new Mock<IGitHubService>();

        var profile = await Detect(github, serviceDirectory: "backend/src");

        Assert.True(profile.IsInconclusive);
        Assert.False(profile.RequiresPostgres);
        Assert.False(profile.RequiresRedis);
    }

    [Fact]
    public async Task EnsureFromRepo_ReportsAnInconclusiveScanForTheDeployLog()
    {
        var github = new Mock<IGitHubService>();
        var (service, project, target) = Build(github);

        var note = await service.EnsureFromRepoAsync(project, target, Branch, CancellationToken.None);

        Assert.NotNull(note);
        Assert.Contains("Could not read", note);
    }

    [Fact]
    public async Task EnsureFromRepo_SaysNothingWhenTheAppGenuinelyNeedsNoDatabase()
    {
        // A readable repo with no database evidence is not news, and a line every deploy for every
        // app is how a log stops being read.
        var github = Monorepo("""{ "Logging": { "LogLevel": { "Default": "Information" } } }""");
        var (service, project, target) = Build(github);

        var note = await service.EnsureFromRepoAsync(project, target, Branch, CancellationToken.None);

        Assert.Null(note);
    }

    [Fact]
    public void BuildVariableLinks_UsesRailwayReferenceSyntax()
    {
        var links = RailwayDatabaseProvisioningService.BuildVariableLinks("Postgres", "Redis");

        // Only the generic default connection strings — the app-specific Admin/Tenant/Test
        // links (idaara multi-tenant) no longer leak into every deployment.
        Assert.Equal(3, links.Count);
        Assert.Equal("ConnectionStrings__Default", links[0].Key);
        Assert.Contains("Postgres.POSTGRES_DB", links[0].ReferenceValue, StringComparison.Ordinal);
        Assert.Equal("ConnectionStrings__DefaultConnection", links[1].Key);
        Assert.Contains("Postgres.POSTGRES_DB", links[1].ReferenceValue, StringComparison.Ordinal);
        Assert.Equal("ConnectionStrings__Redis", links[2].Key);
        Assert.Equal("${{Redis.REDIS_URL}}", links[2].ReferenceValue);
        Assert.DoesNotContain(links, link => link.ReferenceValue.Contains("idaara_test", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildVariableLinks_SkipsMissingServices()
    {
        var links = RailwayDatabaseProvisioningService.BuildVariableLinks("Postgres", null);

        Assert.Equal(2, links.Count);
        Assert.Equal("ConnectionStrings__Default", links[0].Key);
        Assert.Contains("Postgres.RAILWAY_PRIVATE_DOMAIN", links[0].ReferenceValue, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCoolifyVariableLinks_IncludesPostgresAndRedisDatabaseIds()
    {
        var links = RailwayDatabaseProvisioningService.BuildCoolifyVariableLinks(
            new ProvisionedDatabaseService("db-pg", "my-api-postgres", "proj-1", "env-1"),
            new ProvisionedDatabaseService("db-redis", "my-api-redis", "proj-1", "env-1"));

        Assert.Equal(5, links.Count);
        Assert.Contains(links, link => link.Key == "DATABASE_URL" && link.ReferenceValue == "db-pg");
        Assert.Contains(links, link => link.Key == "ConnectionStrings__Redis" && link.ReferenceValue == "db-redis");
        Assert.Contains(links, link => link.Key == "REDIS_URL" && link.ReferenceValue == "db-redis");
    }

    [Fact]
    // yemenConnect reads ConnectionStrings:Postgres and ConnectionStrings:Redis (not the
    // conventional "Default"), so the provisioned databases must be wired to those exact keys or
    // the app never sees them.
    public void BuildCoolifyVariableLinks_WiresDetectedConnectionStringKeys()
    {
        var links = RailwayDatabaseProvisioningService.BuildCoolifyVariableLinks(
            new ProvisionedDatabaseService("db-pg", "yemenhub-postgres", "proj-1", "env-1"),
            new ProvisionedDatabaseService("db-redis", "yemenhub-redis", "proj-1", "env-1"),
            detectedConnectionStringKeys: ["Postgres", "Redis"]);

        // The app's own key gets the Postgres service; the Redis-named key gets Redis.
        Assert.Contains(links, link => link.Key == "ConnectionStrings__Postgres" && link.ReferenceValue == "db-pg");
        Assert.Contains(links, link => link.Key == "ConnectionStrings__Redis" && link.ReferenceValue == "db-redis");
        // Still keyless-safe: no duplicate ConnectionStrings__Redis.
        Assert.Single(links, link => link.Key == "ConnectionStrings__Redis");
    }

    private static async Task<DeployAI.Core.Deployments.DatabaseRequirementProfile> Detect(
        Mock<IGitHubService> github,
        string serviceDirectory)
    {
        var (service, project, target) = Build(github, serviceDirectory);
        return await service.DetectRequirementsAsync(project, target, Branch, CancellationToken.None);
    }

    /// <summary>
    /// yemenConnect's shape: the service directory holds project directories, and appsettings.json
    /// sits inside the one that is actually an application.
    /// </summary>
    private static Mock<IGitHubService> Monorepo(string? appsettings)
    {
        var github = new Mock<IGitHubService>();
        StubDir(github, "backend/src", Dir("backend/src/YemenHub.Api"));
        StubDir(github, "backend/src/YemenHub.Api",
            File("backend/src/YemenHub.Api/YemenHub.Api.csproj"),
            File("backend/src/YemenHub.Api/appsettings.json"));
        StubFile(github, "backend/src/YemenHub.Api/YemenHub.Api.csproj", WebCsproj);

        if (appsettings is not null)
        {
            StubFile(github, "backend/src/YemenHub.Api/appsettings.json", appsettings);
        }

        return github;
    }

    private static (RailwayDatabaseProvisioningService Service, Project Project, DeployTarget Target) Build(
        Mock<IGitHubService> github,
        string serviceDirectory = "backend/src")
    {
        var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = Owner,
            GitHubTokenEncrypted = [1],
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = Repo,
            GitHubRepoFullName = $"{Owner}/{Repo}",
            DefaultBranch = Branch,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var target = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ProviderName = ProviderNameValues.Coolify,
            ProviderProjectId = "api-uuid",
            ConfigJson = $$"""{"role":"server","serviceDirectory":"{{serviceDirectory}}"}""",
            CreatedAt = DateTimeOffset.UtcNow
        };
        project.DeployTargets.Add(target);

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("gh-token");

        var provisioningFactory = new Mock<IProviderDatabaseProvisioningFactory>();
        provisioningFactory
            .Setup(f => f.GetProvisioning(It.IsAny<string>()))
            .Returns(Mock.Of<IProviderDatabaseProvisioning>());

        var resolver = new RepositoryLayoutResolver(github.Object);

        var service = new RailwayDatabaseProvisioningService(
            db,
            provisioningFactory.Object,
            Mock.Of<IProviderManagementFactory>(),
            Mock.Of<IProviderServiceOperationsFactory>(),
            Mock.Of<IProviderCredentialTokenService>(),
            resolver,
            resolver,
            encryption.Object,
            new DatabaseRequirementDetector(),
            NullLogger<RailwayDatabaseProvisioningService>.Instance);

        return (service, project, target);
    }

    private static GitHubContentItem Dir(string path) => new(Path.GetFileName(path), path, "dir");

    private static GitHubContentItem File(string path) => new(Path.GetFileName(path), path, "file");

    private static void StubDir(Mock<IGitHubService> github, string path, params GitHubContentItem[] items) =>
        github.Setup(g => g.ListAllContentsAsync(
                It.IsAny<string>(), Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

    private static void StubFile(Mock<IGitHubService> github, string path, string content) =>
        github.Setup(g => g.GetFileContentAsync(
                It.IsAny<string>(), Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
}
