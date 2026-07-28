using System.Net;
using System.Net.Http.Json;
using DeployAI.Core.Providers;
using DeployAI.Providers.Coolify;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

public class CoolifyProviderDatabaseTests
{
    private const string InstanceUrl = "https://coolify.example.com";
    private const string ApiToken = "coolify-token";
    private static readonly ProviderCredentials Credentials =
        new(CoolifyCredentialStorage.Serialize(InstanceUrl, ApiToken));

    private static CoolifyProvider CreateProvider(MockHttpMessageHandler handler)
    {
        var client = handler.ToHttpClient();
        return new CoolifyProvider(client);
    }

    [Fact]
    public async Task EnsurePostgresAsync_CreatesDatabase_WhenNoneExists()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-api")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "uuid": "app-api",
              "name": "my-api",
              "project_uuid": "proj-1",
              "server_uuid": "server-1",
              "environment_name": "production",
              "environment_uuid": "env-1"
            }
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/databases/postgresql")
            .Respond(HttpStatusCode.Created, "application/json", """{ "uuid": "db-new" }""");

        var provider = CreateProvider(handler);
        var result = await provider.EnsurePostgresAsync(
            Credentials,
            "app-api",
            "mydb",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("db-new", result!.ServiceId);
        Assert.Equal("mydb", result.ServiceName);
    }

    [Fact]
    public async Task ResolveApplicationUrlAsync_ReturnsNormalizedFqdn()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-api")
            .Respond(HttpStatusCode.OK, "application/json", """
            { "uuid": "app-api", "fqdn": "api.example.com" }
            """);

        var provider = CreateProvider(handler);
        var url = await provider.ResolveApplicationUrlAsync(
            Credentials,
            "app-api",
            CancellationToken.None);

        Assert.Equal("https://api.example.com", url);
    }

    [Fact]
    public async Task LinkDatabaseVariablesAsync_UpsertsConnectionString()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases/db-1")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "postgres_connection_string": "postgres://user:pass@db:5432/app"
            }
            """);
        handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-api/envs/bulk")
            .Respond(HttpStatusCode.Created, "application/json", """[{ "uuid": "env-1", "key": "DATABASE_URL" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-api/envs")
            .Respond("application/json", """
            [
              { "uuid": "env-1", "key": "DATABASE_URL", "value": "postgres://user:pass@db:5432/app" }
            ]
            """);

        var provider = CreateProvider(handler);
        var exception = await Record.ExceptionAsync(() => provider.LinkDatabaseVariablesAsync(
            Credentials,
            "app-api",
            [new DatabaseVariableLink("DATABASE_URL", "db-1")],
            CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task LinkDatabaseVariablesAsync_RemovesDuplicateRecordsLeftByEarlierRuns()
    {
        // This is the path the duplicates arrived on, back when the upsert read before it wrote,
        // and the one where they do the most damage: a stale second DATABASE_URL points the
        // container at a database that may no longer exist. An affected app should be repaired by
        // being deployed, not by someone deleting rows by hand in Coolify's UI.
        //
        // env-1 (the survivor) has no DELETE handler, so deleting it fails this test.
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases/db-1")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "postgres_connection_string": "postgres://user:pass@db:5432/app"
            }
            """);
        handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-api/envs/bulk")
            .Respond(HttpStatusCode.Created, "application/json", """[{ "uuid": "env-1", "key": "DATABASE_URL" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-api/envs")
            .Respond("application/json", """
            [
              { "uuid": "env-1", "key": "DATABASE_URL", "value": "postgres://user:pass@db:5432/app" },
              { "uuid": "env-stale", "key": "DATABASE_URL", "value": "postgres://user:pass@gone:5432/app" },
              { "uuid": "env-2", "key": "POSTGRES_URL", "value": "postgres://user:pass@db:5432/app" }
            ]
            """);
        var deleteStaleCopy = handler
            .When(HttpMethod.Delete, $"{InstanceUrl}/api/v1/applications/app-api/envs/env-stale")
            .Respond(HttpStatusCode.OK, "application/json", "{}");

        var provider = CreateProvider(handler);
        await provider.LinkDatabaseVariablesAsync(
            Credentials,
            "app-api",
            [new DatabaseVariableLink("DATABASE_URL", "db-1")],
            CancellationToken.None);

        Assert.Equal(1, handler.GetMatchCount(deleteStaleCopy));
    }

    [Fact]
    public async Task EnsureRedisAsync_CreatesDatabase_WhenNoneExists()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-api")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "uuid": "app-api",
              "name": "my-api",
              "project_uuid": "proj-1",
              "server_uuid": "server-1",
              "environment_name": "production",
              "environment_uuid": "env-1"
            }
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/databases/redis")
            .Respond(HttpStatusCode.Created, "application/json", """{ "uuid": "redis-new" }""");

        var provider = CreateProvider(handler);
        var result = await provider.EnsureRedisAsync(
            Credentials,
            "app-api",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("redis-new", result!.ServiceId);
        Assert.Equal("my-api-redis", result.ServiceName);
    }

    [Fact]
    public async Task DeleteDatabaseAsync_DeletesByUuid()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Delete, $"{InstanceUrl}/api/v1/databases/db-1")
            .Respond(HttpStatusCode.OK, "application/json", """{ "message": "Database deleted." }""");

        var provider = CreateProvider(handler);
        await provider.DeleteDatabaseAsync(
            Credentials,
            "db-1|env-1",
            CancellationToken.None);

        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task LinkDatabaseVariablesAsync_UpsertsRedisConnectionString()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases/db-redis")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "internal_db_url": "redis://:secret@redis:6379"
            }
            """);
        handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-api/envs/bulk")
            .Respond(HttpStatusCode.Created, "application/json", """[{ "uuid": "env-1", "key": "ConnectionStrings__Redis" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-api/envs")
            .Respond("application/json", """
            [
              { "uuid": "env-1", "key": "ConnectionStrings__Redis", "value": "redis://:secret@redis:6379" }
            ]
            """);

        var provider = CreateProvider(handler);
        var exception = await Record.ExceptionAsync(() => provider.LinkDatabaseVariablesAsync(
            Credentials,
            "app-api",
            [new DatabaseVariableLink("ConnectionStrings__Redis", "db-redis")],
            CancellationToken.None));

        Assert.Null(exception);
    }
}
