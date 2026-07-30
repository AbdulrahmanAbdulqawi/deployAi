using System.Text;
using System.Text.Json;
using DeployAI.Api.Controllers;
using DeployAI.Api.Services;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// A signing key DeployAI generates is written to the provider as a secret, which Coolify will not
/// read back (is_shown_once), and stored encrypted in DeployAI's own database. If that database is
/// lost — which has happened — the value is unrecoverable and every token it signed breaks.
/// Generating a secret on someone's behalf implies keeping it recoverable.
/// </summary>
public class EnvironmentExportTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task Export_IncludesTheSecretsOnlyDeployAIHas()
    {
        var (controller, projectId) = Create(
            stored: new() { ["Jwt__SigningKey"] = ("s3cret-signing-key", true) },
            live: [Hidden("Jwt__SigningKey")]);

        var content = await ExportAsync(controller, projectId);

        Assert.Contains("Jwt__SigningKey=s3cret-signing-key", content);
        Assert.Contains("only in DeployAI", content);
    }

    [Fact]
    public async Task Export_SaysWhenAValueCannotBeRecovered_RatherThanWritingItBlank()
    {
        // Held write-only by the provider and never by DeployAI. Writing "KEY=" would look like a
        // real value and restore an empty one.
        var (controller, projectId) = Create(
            stored: [],
            live: [Hidden("SET_BY_HAND")]);

        var content = await ExportAsync(controller, projectId);

        Assert.DoesNotContain("SET_BY_HAND=\n", content);
        Assert.Contains("# SET_BY_HAND=(not in this export", content);
    }

    [Fact]
    public async Task Export_MarksItselfIncomplete_WhenAnAppCouldNotBeRead()
    {
        // An export that quietly omits an app looks like a full backup and restores a partial one.
        var (controller, projectId) = Create(
            stored: new() { ["Jwt__SigningKey"] = ("k", true) },
            live: [],
            listThrows: new InvalidOperationException("Coolify returned 404."));

        var content = await ExportAsync(controller, projectId);

        Assert.Contains("INCOMPLETE", content);
        Assert.Contains("Do not treat it as a full backup", content);
    }

    [Fact]
    public async Task Export_QuotesValuesThatWouldNotSurviveARoundTrip()
    {
        var (controller, projectId) = Create(
            stored: new() { ["Storage__SecretKey"] = ("has space$and#hash", true) },
            live: []);

        var content = await ExportAsync(controller, projectId);

        Assert.Contains("Storage__SecretKey=\"has space$and#hash\"", content);
    }

    [Fact]
    public async Task Export_CollapsesDuplicateRecords_AndSaysThatItDid()
    {
        // A provider can hold several records for one key -- one app was seen with 32 records for
        // 16 keys -- and a .env with the same key twice restores whichever line is read last.
        // Collapsing quietly would hide a real problem, so the file names what it collapsed.
        var (controller, projectId) = Create(
            stored: [],
            live: [Plain("API_URL", "https://api.example.com"), Plain("API_URL", "https://api.example.com")]);

        var content = await ExportAsync(controller, projectId);

        Assert.Single(content.Split('\n').Where(l => l.StartsWith("API_URL=")));
        Assert.Contains("had duplicate records", content);
    }

    [Fact]
    public async Task Export_GroupsByApp_SoARestoreKnowsWhereEachValueBelongs()
    {
        var (controller, projectId) = Create(
            stored: [],
            live: [Plain("API_URL", "https://api.example.com")]);

        var content = await ExportAsync(controller, projectId);

        Assert.Contains("--- server ---", content);
    }

    private static async Task<string> ExportAsync(ProjectEnvironmentController controller, Guid projectId)
    {
        var result = Assert.IsType<FileContentResult>(
            await controller.ExportEnvironment(projectId, CancellationToken.None));

        Assert.Equal("text/plain; charset=utf-8", result.ContentType);
        return Encoding.UTF8.GetString(result.FileContents);
    }

    private static ProviderEnvVar Hidden(string key) =>
        new(Guid.NewGuid().ToString(), key, null, ProviderEnvVarTypes.Secret, [], ValueHidden: true);

    private static ProviderEnvVar Plain(string key, string value) =>
        new(Guid.NewGuid().ToString(), key, value, ProviderEnvVarTypes.Plain, [], ValueHidden: false);

    private static (ProjectEnvironmentController, Guid) Create(
        Dictionary<string, (string Value, bool IsSecret)> stored,
        IReadOnlyList<ProviderEnvVar> live,
        Exception? listThrows = null)
    {
        var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var projectId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();

        db.ProviderCredentials.Add(new ProviderCredential
        {
            Id = credentialId,
            UserId = UserId,
            ProviderName = "coolify",
            TokenEncrypted = [1],
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = UserId,
            Name = "yemenConnect",
            GitHubRepoFullName = "tester/yemenConnect",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            EnvironmentVariablesEncrypted = stored.Count > 0 ? [1] : null,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    CredentialId = credentialId,
                    ProviderProjectId = "api-uuid",
                    ConfigJson = """{"role":"server"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns(
            JsonSerializer.Serialize(stored.ToDictionary(kv => kv.Key, kv => new { kv.Value.Value, kv.Value.IsSecret })));

        var management = new Mock<IProviderManagement>();
        var setup = management.Setup(m => m.ListEnvVarsAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));
        if (listThrows is not null)
        {
            setup.ThrowsAsync(listThrows);
        }
        else
        {
            setup.ReturnsAsync(live);
        }

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement(It.IsAny<string>())).Returns(management.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.UserId).Returns(UserId);

        return (
            new ProjectEnvironmentController(
                db,
                currentUser.Object,
                Mock.Of<IFrontendEnvironmentWiringService>(),
                factory.Object,
                encryption.Object,
                Mock.Of<IProviderRuntimeLogsFactory>(),
                tokens.Object,
                new MissingConfigurationDetector(),
                NullLogger<ProjectEnvironmentController>.Instance),
            projectId);
    }
}
