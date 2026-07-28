using System.Text.Json;
using DeployAI.Api.Controllers;
using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class MissingConfigurationEndpointTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task GetMissingConfiguration_ReportsWhatTheContainerSaidItNeeded()
    {
        var (controller, projectId, targetId) = Build(
            logs: "Unhandled exception. System.InvalidOperationException: Jwt configuration missing.\n"
                + "   at Program.<Main>$(String[] args) in /src/YemenHub.Api/Program.cs:line 94");

        var body = await Invoke(controller, projectId, targetId);

        Assert.True(GetBool(body, "readable"));
        var missing = GetArray(body, "missing");
        var entry = Assert.Single(missing);
        Assert.Equal("Jwt", Prop(entry, "name"));
        Assert.Equal("section", Prop(entry, "kind"));
        Assert.False(string.IsNullOrWhiteSpace(Prop(entry, "suggestedValue")));
    }

    [Fact]
    public async Task GetMissingConfiguration_LeavesOutKeysThatAreAlreadySet()
    {
        // Offering to overwrite a working secret is how you break a running app, and a key that is
        // set is by definition not the one missing.
        var (controller, projectId, targetId) = Build(
            logs: "System.InvalidOperationException: Jwt configuration missing.",
            storedKeys: ["Jwt"]);

        var body = await Invoke(controller, projectId, targetId);

        Assert.True(GetBool(body, "readable"));
        Assert.Empty(GetArray(body, "missing"));
    }

    [Fact]
    public async Task GetMissingConfiguration_SaysSoWhenTheLogsCannotBeRead()
    {
        // A stopped container is exactly the state this feature exists for, and Coolify refuses
        // logs for one. Returning an empty list would read as "your configuration is fine" — the
        // caller has to be able to tell "nothing missing" from "I could not look".
        var (controller, projectId, targetId) = Build(
            logsThrow: new DeployAIException("coolify_api_error", "Application is not running."));

        var body = await Invoke(controller, projectId, targetId);

        Assert.False(GetBool(body, "readable"));
        Assert.Empty(GetArray(body, "missing"));
        Assert.Equal("coolify_api_error", Prop(body, "reason"));
    }

    [Fact]
    public async Task GetMissingConfiguration_SaysSoWhenTheProviderHasNoRuntimeLogs()
    {
        var (controller, projectId, targetId) = Build(logs: null, supportsLogs: false);

        var body = await Invoke(controller, projectId, targetId);

        Assert.False(GetBool(body, "readable"));
        Assert.Equal("unsupported_provider", Prop(body, "reason"));
    }

    private static async Task<JsonElement> Invoke(
        ProjectEnvironmentController controller,
        Guid projectId,
        Guid targetId)
    {
        var result = await controller.GetMissingConfiguration(projectId, targetId, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    private static (ProjectEnvironmentController Controller, Guid ProjectId, Guid TargetId) Build(
        string? logs = null,
        Exception? logsThrow = null,
        string[]? storedKeys = null,
        bool supportsLogs = true)
    {
        var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var projectId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();

        var encryption = new Mock<IEncryptionService>();
        var storedBlob = JsonSerializer.Serialize(
            (storedKeys ?? []).ToDictionary(k => k, _ => new { Value = "set", IsSecret = true }));
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns(storedBlob);

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
            EnvironmentVariablesEncrypted = storedKeys is { Length: > 0 } ? [1] : null,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = targetId,
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    CredentialId = credentialId,
                    ProviderProjectId = "app-uuid",
                    ConfigJson = """{"role":"server"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });
        db.SaveChanges();

        // Forces every read to go through a query rather than the graph these Add calls left in the
        // tracker. Without it the Credential navigation is populated for free and a missing Include
        // passes the test while throwing NullReferenceException against a real database — which is
        // exactly what happened the first time this endpoint was run.
        db.ChangeTracker.Clear();

        var runtimeLogs = new Mock<IProviderRuntimeLogs>();
        var setup = runtimeLogs.Setup(r => r.GetRuntimeLogsAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()));
        if (logsThrow is not null)
        {
            setup.ThrowsAsync(logsThrow);
        }
        else
        {
            setup.ReturnsAsync(logs ?? string.Empty);
        }

        var logsFactory = new Mock<IProviderRuntimeLogsFactory>();
        logsFactory.Setup(f => f.GetRuntimeLogs(It.IsAny<string>()))
            .Returns(supportsLogs ? runtimeLogs.Object : null);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .Returns((ProviderCredential credential, CancellationToken _) => credential is null
                // The real service dereferences this. A permissive mock let a missing Include pass
                // here and then throw NullReferenceException against a real database.
                ? throw new InvalidOperationException("The deploy target's credential was not loaded.")
                : Task.FromResult("token"));

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.UserId).Returns(UserId);

        var controller = new ProjectEnvironmentController(
            db,
            currentUser.Object,
            Mock.Of<IFrontendEnvironmentWiringService>(),
            Mock.Of<IProviderManagementFactory>(),
            encryption.Object,
            logsFactory.Object,
            tokens.Object,
            new MissingConfigurationDetector());

        return (controller, projectId, targetId);
    }

    private static JsonElement[] GetArray(JsonElement body, string name) =>
        body.GetProperty(name).EnumerateArray().ToArray();

    private static bool GetBool(JsonElement body, string name) => body.GetProperty(name).GetBoolean();

    private static string Prop(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? string.Empty;
}
