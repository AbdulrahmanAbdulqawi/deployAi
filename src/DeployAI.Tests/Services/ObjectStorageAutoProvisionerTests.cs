using DeployAI.Api.Services;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// Deploy-time storage detection, against the repository shape it has to work on.
/// </summary>
public class ObjectStorageAutoProvisionerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private const string AppsettingsWithStorage = """
        {
          "Storage": { "Bucket": "yemenhub-media", "AccessKey": "", "SecretKey": "" }
        }
        """;

    [Fact]
    public async Task EnsureAsync_FindsTheNeed_WhenTheApiIsNestedUnderTheServiceDirectory()
    {
        // yemenConnect's shape, and the reason this test exists: serviceDirectory is backend/src,
        // which holds four project directories and no files of its own. Reading only that level
        // found no appsettings.json and no csproj, so detection saw nothing, no bucket was
        // provisioned, and the app would have gone on writing uploads into its container.
        var github = MonorepoRepository();

        var (provisioner, project, target) = Create(github);
        var outcome = await provisioner.EnsureAsync(project, target, "main", CancellationToken.None);

        Assert.True(outcome.Needed);
        Assert.NotNull(outcome.Message);
        Assert.Contains("Provisioned object storage", outcome.Message);
    }

    [Fact]
    public async Task EnsureAsync_SaysUploadsWillBeLost_WhenNoStorageIsConnected()
    {
        // The keys are the user's own, so DeployAI cannot invent them. Silence here is what leaves
        // an app accepting uploads it will lose on the next deploy.
        var (provisioner, project, target) = Create(MonorepoRepository(), withStorageConnection: false);

        var outcome = await provisioner.EnsureAsync(project, target, "main", CancellationToken.None);

        Assert.True(outcome.Needed);
        Assert.Contains("no object storage is connected", outcome.Message);
        Assert.Contains("lost on the next deploy", outcome.Message);
    }

    [Fact]
    public async Task EnsureAsync_StaysQuiet_ForAnAppThatStoresNoFiles()
    {
        var github = new Mock<IGitHubService>();
        StubDir(github, "backend/src", Dir("backend/src/YemenHub.Api"));
        StubDir(github, "backend/src/YemenHub.Api", File("backend/src/YemenHub.Api/appsettings.json"));
        StubFile(github, "backend/src/YemenHub.Api/appsettings.json",
            """{"ConnectionStrings":{"Postgres":"Host=db"}}""");

        var (provisioner, project, target) = Create(github);
        var outcome = await provisioner.EnsureAsync(project, target, "main", CancellationToken.None);

        Assert.False(outcome.Needed);
        Assert.Null(outcome.Message);
    }

    /// <summary>backend/src holds only directories; the API's files live one level down.</summary>
    private static Mock<IGitHubService> MonorepoRepository()
    {
        var github = new Mock<IGitHubService>();
        StubDir(github, "backend/src",
            Dir("backend/src/YemenHub.Api"),
            Dir("backend/src/YemenHub.Persistence"));
        StubDir(github, "backend/src/YemenHub.Api",
            File("backend/src/YemenHub.Api/appsettings.json"),
            File("backend/src/YemenHub.Api/YemenHub.Api.csproj"));
        StubDir(github, "backend/src/YemenHub.Persistence",
            File("backend/src/YemenHub.Persistence/YemenHub.Persistence.csproj"));

        StubFile(github, "backend/src/YemenHub.Api/appsettings.json", AppsettingsWithStorage);
        StubFile(github, "backend/src/YemenHub.Api/YemenHub.Api.csproj",
            """<Project><ItemGroup><PackageReference Include="AWSSDK.S3" Version="3.7.0" /></ItemGroup></Project>""");
        StubFile(github, "backend/src/YemenHub.Persistence/YemenHub.Persistence.csproj", "<Project />");
        return github;
    }

    private static (ObjectStorageAutoProvisioner, Project, DeployTarget) Create(
        Mock<IGitHubService> github,
        bool withStorageConnection = true)
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

        if (withStorageConnection)
        {
            db.ProviderCredentials.Add(new ProviderCredential
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                ProviderName = "hetzner-storage",
                Kind = CredentialKind.ObjectStorage,
                TokenEncrypted = [2],
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        db.SaveChanges();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = "yemenConnect",
            GitHubRepoFullName = "tester/yemenConnect",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var target = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ProviderName = "coolify",
            ProviderProjectId = "api-uuid",
            ConfigJson = """{"role":"server","serviceDirectory":"backend/src","framework":"docker"}""",
            CreatedAt = DateTimeOffset.UtcNow
        };
        project.DeployTargets.Add(target);

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("gh-token");

        var provisioning = new Mock<IObjectStorageProvisioningService>();
        provisioning.Setup(p => p.ProvisionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ObjectStorageProvisioningResult("yemenconnect", ["Storage__Bucket"]));

        return (
            new ObjectStorageAutoProvisioner(
                db,
                github.Object,
                encryption.Object,
                new ObjectStorageNeedDetector(),
                provisioning.Object,
                NullLogger<ObjectStorageAutoProvisioner>.Instance),
            project,
            target);
    }

    private static GitHubContentItem Dir(string path) => new(Path.GetFileName(path), path, "dir");

    private static GitHubContentItem File(string path) => new(Path.GetFileName(path), path, "file");

    private static void StubDir(Mock<IGitHubService> github, string path, params GitHubContentItem[] items) =>
        github.Setup(g => g.ListAllContentsAsync(
                It.IsAny<string>(), "tester", "yemenConnect", path, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

    private static void StubFile(Mock<IGitHubService> github, string path, string content) =>
        github.Setup(g => g.GetFileContentAsync(
                It.IsAny<string>(), "tester", "yemenConnect", path, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
}
