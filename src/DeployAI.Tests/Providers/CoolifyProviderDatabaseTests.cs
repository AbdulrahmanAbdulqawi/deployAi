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
    // Real Coolify (v4) does not return project_uuid/server_uuid/environment_name on the
    // application payload — only a numeric environment_id and a destination. The provider must
    // resolve the project/environment by matching that id against each project's environments,
    // and fall back to the single server. Without this, DB provisioning silently no-ops.
    public async Task EnsurePostgresAsync_ResolvesProjectAndServer_FromEnvironmentId_WhenFlatFieldsMissing()
    {
        string? createBody = null;
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-api")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "uuid": "app-api",
              "name": "yemenconnect-api",
              "environment_id": 6,
              "build_pack": "dockerfile"
            }
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "server-1", "name": "homelab" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "proj-1", "name": "smoke" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects/proj-1/environments")
            .Respond(HttpStatusCode.OK, "application/json", """
            [{ "id": 6, "uuid": "env-uuid-6", "name": "production" }]
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/databases/postgresql")
            .Respond(async req =>
            {
                createBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{ "uuid": "db-new" }""")
                };
            });

        var provider = CreateProvider(handler);
        var result = await provider.EnsurePostgresAsync(
            Credentials,
            "app-api",
            "yemenhub",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("db-new", result!.ServiceId);
        Assert.NotNull(createBody);
        // The create must target the resolved project / environment / server, not blanks.
        Assert.Contains("\"project_uuid\":\"proj-1\"", createBody);
        Assert.Contains("\"server_uuid\":\"server-1\"", createBody);
        Assert.Contains("\"environment_name\":\"production\"", createBody);
        Assert.Contains("\"environment_uuid\":\"env-uuid-6\"", createBody);
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

    [Theory]
    // Npgsql cannot parse a postgres:// URI out of ASP.NET configuration — it needs keywords.
    [InlineData("postgres://user:pass@db:5432/app", "Host=db;Port=5432;Database=app;Username=user;Password=pass")]
    [InlineData("postgresql://u%40x:p%3Aw@host:5433/my-db", "Host=host;Port=5433;Database=my-db;Username=u@x;Password=p:w")]
    // StackExchange.Redis wants host:port with options, not a redis:// URI.
    [InlineData("redis://:secret@redis:6379", "redis:6379,password=secret")]
    [InlineData("redis://redis:6380", "redis:6380")]
    [InlineData("rediss://:secret@redis:6379", "redis:6379,password=secret,ssl=true")]
    // Already-keyword values and unknown schemes pass through untouched.
    [InlineData("Host=db;Database=app;Username=u;Password=p", "Host=db;Database=app;Username=u;Password=p")]
    [InlineData("mysql://user:pass@db:3306/app", "mysql://user:pass@db:3306/app")]
    public void ConvertUriToDotnetConnectionString_ConvertsPerScheme(string input, string expected)
    {
        Assert.Equal(expected, CoolifyProvider.ConvertUriToDotnetConnectionString(input));
    }

    [Fact]
    public async Task LinkDatabaseVariablesAsync_ConvertsUriForConnectionStringsKeys()
    {
        string? upsertBody = null;
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
            .Respond(async req =>
            {
                upsertBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{ "uuid": "env-1", "key": "ConnectionStrings__Default" }""")
                };
            });

        var provider = CreateProvider(handler);
        await provider.LinkDatabaseVariablesAsync(
            Credentials,
            "app-api",
            [new DatabaseVariableLink("ConnectionStrings__Default", "db-1")],
            CancellationToken.None);

        Assert.NotNull(upsertBody);
        Assert.Contains("Host=db;Port=5432;Database=app;Username=user;Password=pass", upsertBody);
        Assert.DoesNotContain("postgres://", upsertBody);
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
