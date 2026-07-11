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
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-api/envs")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/app-api/envs")
            .Respond(HttpStatusCode.Created, "application/json", """{ "uuid": "env-1", "key": "DATABASE_URL" }""");

        var provider = CreateProvider(handler);
        var exception = await Record.ExceptionAsync(() => provider.LinkDatabaseVariablesAsync(
            Credentials,
            "app-api",
            [new DatabaseVariableLink("DATABASE_URL", "db-1")],
            CancellationToken.None));

        Assert.Null(exception);
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
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-api/envs")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/app-api/envs")
            .Respond(HttpStatusCode.Created, "application/json", """{ "uuid": "env-1", "key": "ConnectionStrings__Redis" }""");

        var provider = CreateProvider(handler);
        var exception = await Record.ExceptionAsync(() => provider.LinkDatabaseVariablesAsync(
            Credentials,
            "app-api",
            [new DatabaseVariableLink("ConnectionStrings__Redis", "db-redis")],
            CancellationToken.None));

        Assert.Null(exception);
    }
}
