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
/// The environment screen used to list DeployAI's own encrypted store and nothing else, so it showed
/// only the handful of keys someone had typed into it. Everything the app actually runs on — the
/// database URL DeployAI linked, the API URL it derived, the CORS origins it wired — was invisible.
/// One deployed API had fifteen variables and the screen showed four.
/// </summary>
public class EnvironmentListingTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task ListEnvironment_ReturnsWhatEachAppActuallyHas_NotJustWhatDeployAIStored()
    {
        var harness = Harness.Create(
            stored: new() { ["Jwt__SigningKey"] = ("typed-in", true) },
            website: [Var("NEXT_PUBLIC_API_URL", "https://api.example.com")],
            server: [Var("DATABASE_URL", "postgres://u:p@db:5432/x"), Var("Cors__Origins__0", "https://site.example.com")]);

        var body = await harness.ListAsync();
        var keys = Keys(body).ToList();

        // All three live keys, none of which DeployAI had in its store.
        Assert.Contains("NEXT_PUBLIC_API_URL", keys);
        Assert.Contains("DATABASE_URL", keys);
        Assert.Contains("Cors__Origins__0", keys);
    }

    [Fact]
    public async Task ListEnvironment_SaysWhichAppEachVariableIsOn()
    {
        // The single project-wide store cannot answer this, and it is the question that matters on a
        // split deploy: a server setting written to the website is read by nothing.
        var harness = Harness.Create(
            stored: [],
            website: [Var("NEXT_PUBLIC_API_URL", "https://api.example.com")],
            server: [Var("Cors__Origins__0", "https://site.example.com")]);

        var body = await harness.ListAsync();

        Assert.Equal("website", RoleOf(body, "NEXT_PUBLIC_API_URL"));
        Assert.Equal("server", RoleOf(body, "Cors__Origins__0"));
    }

    [Fact]
    public async Task ListEnvironment_ReportsATargetItCouldNotRead_RatherThanReturningAShorterList()
    {
        // A short list that looks complete is how a missing variable gets mistaken for a set one.
        var harness = Harness.Create(
            stored: [],
            website: [Var("NEXT_PUBLIC_API_URL", "https://api.example.com")],
            serverThrows: new InvalidOperationException("Coolify returned 404."));

        var body = await harness.ListAsync();

        var unreadable = body.GetProperty("unreadable");
        Assert.Equal(1, unreadable.GetArrayLength());
        Assert.Equal("server", unreadable[0].GetProperty("targetRole").GetString());
        Assert.Contains("404", unreadable[0].GetProperty("reason").GetString());
        // The readable target's variables still come back.
        Assert.Contains("NEXT_PUBLIC_API_URL", Keys(body));
    }

    [Fact]
    public async Task ListEnvironment_MasksASecretLookingValue_EvenWhenTheProviderDoesNotSayItIsOne()
    {
        // Reading a value back from a provider loses whether it was ever meant to be secret. A
        // connection string rendered in clear on a shared screen is the failure that matters.
        var harness = Harness.Create(
            stored: [],
            website: [],
            server: [Var("DATABASE_URL", "postgres://user:hunter2@db:5432/app"), Var("ASPNETCORE_URLS", "http://+:8080")]);

        var body = await harness.ListAsync();

        Assert.True(IsSecret(body, "DATABASE_URL"));
        Assert.False(IsSecret(body, "ASPNETCORE_URLS"));
    }

    [Fact]
    public async Task ListEnvironment_StillShowsAStoredVariableNoAppReportsBack()
    {
        // Either its app could not be read, or the value never reached the provider. Both are worth
        // seeing; dropping it would hide a setting the user believes is applied.
        var harness = Harness.Create(
            stored: new() { ["Jwt__SigningKey"] = ("typed-in", true) },
            website: [],
            server: []);

        var body = await harness.ListAsync();

        var row = Row(body, "Jwt__SigningKey");
        Assert.Equal(JsonValueKind.Null, row.GetProperty("targetId").ValueKind);
        Assert.True(row.GetProperty("managed").GetBoolean());
    }

    [Fact]
    public void GetGeneratedValue_ProducesAUsablePassword_SoOneIsNeverTypedByHand()
    {
        var harness = Harness.Create(stored: [], website: [], server: []);

        var first = Value(harness.Controller.GetGeneratedValue("Bootstrap__PlatformAdminPassword"));
        var second = Value(harness.Controller.GetGeneratedValue("Bootstrap__PlatformAdminPassword"));

        Assert.NotEqual(first, second);
        // Every character class: ASP.NET Identity rejects an alphanumeric-only password at seed
        // time, which once crash-looped a live deploy.
        Assert.Contains(first, char.IsUpper);
        Assert.Contains(first, char.IsLower);
        Assert.Contains(first, char.IsDigit);
        Assert.Contains(first, c => !char.IsLetterOrDigit(c));
    }

    [Fact]
    public void GetGeneratedValue_RejectsAnEmptyKey()
    {
        var harness = Harness.Create(stored: [], website: [], server: []);

        Assert.Throws<DeployAI.Core.Exceptions.DeployAIException>(
            () => harness.Controller.GetGeneratedValue("  "));
    }

    private static string Value(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value)
            .GetProperty("value").GetString()!;

    private static ProviderEnvVar Var(string key, string value) =>
        new(Guid.NewGuid().ToString(), key, value, ProviderEnvVarTypes.Plain, [], ValueHidden: false);

    private static IEnumerable<string> Keys(JsonElement body) =>
        body.GetProperty("variables").EnumerateArray().Select(v => v.GetProperty("key").GetString()!);

    private static JsonElement Row(JsonElement body, string key) =>
        body.GetProperty("variables").EnumerateArray().First(v => v.GetProperty("key").GetString() == key);

    private static string? RoleOf(JsonElement body, string key) =>
        Row(body, key).GetProperty("targetRole").GetString();

    private static bool IsSecret(JsonElement body, string key) =>
        Row(body, key).GetProperty("isSecret").GetBoolean();

    private sealed class Harness
    {
        public ProjectEnvironmentController Controller { get; init; } = null!;
        public Guid ProjectId { get; init; }

        /// <summary>Serialized the way ASP.NET does it, so the assertions read the names the client sees.</summary>
        private static readonly JsonSerializerOptions AsTheApiReturnsIt =
            new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public async Task<JsonElement> ListAsync()
        {
            var result = Assert.IsType<OkObjectResult>(
                await Controller.ListEnvironment(ProjectId, CancellationToken.None));
            return JsonSerializer.SerializeToElement(result.Value, AsTheApiReturnsIt);
        }

        public static Harness Create(
            Dictionary<string, (string Value, bool IsSecret)> stored,
            IReadOnlyList<ProviderEnvVar> website,
            IReadOnlyList<ProviderEnvVar>? server = null,
            Exception? serverThrows = null)
        {
            var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

            var projectId = Guid.NewGuid();
            var websiteTargetId = Guid.NewGuid();
            var serverTargetId = Guid.NewGuid();
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
                    Target(websiteTargetId, projectId, credentialId, "website", "web-uuid"),
                    Target(serverTargetId, projectId, credentialId, "server", "api-uuid")
                ]
            });
            db.SaveChanges();
            db.ChangeTracker.Clear();

            var encryption = new Mock<IEncryptionService>();
            encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns(
                JsonSerializer.Serialize(stored.ToDictionary(
                    kv => kv.Key,
                    kv => new { kv.Value.Value, kv.Value.IsSecret })));

            var management = new Mock<IProviderManagement>();
            management.Setup(m => m.ListEnvVarsAsync(
                    It.IsAny<ProviderCredentials>(), "web-uuid", It.IsAny<CancellationToken>()))
                .ReturnsAsync(website);

            var serverSetup = management.Setup(m => m.ListEnvVarsAsync(
                It.IsAny<ProviderCredentials>(), "api-uuid", It.IsAny<CancellationToken>()));
            if (serverThrows is not null)
            {
                serverSetup.ThrowsAsync(serverThrows);
            }
            else
            {
                serverSetup.ReturnsAsync(server ?? []);
            }

            var factory = new Mock<IProviderManagementFactory>();
            factory.Setup(f => f.GetManagement(It.IsAny<string>())).Returns(management.Object);

            var tokens = new Mock<IProviderCredentialTokenService>();
            tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
                .Returns((ProviderCredential credential, CancellationToken _) => credential is null
                    // The real code dereferences this; a permissive mock would let a missing Include
                    // pass here and throw against a real database.
                    ? throw new InvalidOperationException("The deploy target's credential was not loaded.")
                    : Task.FromResult("token"));

            var currentUser = new Mock<ICurrentUserService>();
            currentUser.SetupGet(c => c.UserId).Returns(UserId);

            return new Harness
            {
                ProjectId = projectId,
                Controller = new ProjectEnvironmentController(
                    db,
                    currentUser.Object,
                    Mock.Of<IFrontendEnvironmentWiringService>(),
                    factory.Object,
                    encryption.Object,
                    Mock.Of<IProviderRuntimeLogsFactory>(),
                    tokens.Object,
                    new MissingConfigurationDetector(),
                    NullLogger<ProjectEnvironmentController>.Instance)
            };
        }

        private static DeployTarget Target(Guid id, Guid projectId, Guid credentialId, string role, string providerProjectId) =>
            new()
            {
                Id = id,
                ProjectId = projectId,
                ProviderName = "coolify",
                CredentialId = credentialId,
                ProviderProjectId = providerProjectId,
                ConfigJson = $$"""{"role":"{{role}}"}""",
                CreatedAt = DateTimeOffset.UtcNow
            };
    }
}
