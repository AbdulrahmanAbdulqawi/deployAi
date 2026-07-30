using System.Net;
using DeployAI.Core.Providers;
using DeployAI.Providers.Coolify;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

/// <summary>
/// Database inspection was a Railway-only capability, so every Coolify database — the default —
/// answered <c>unsupported_provider</c>. DeployAI provisioned a database and then could not look at
/// it, which is also why "did the migrations apply" has no answer on the provider it defaults to.
/// </summary>
public class CoolifyDataServiceInspectionTests
{
    private const string InstanceUrl = "http://46.225.80.188:8000";
    private static readonly ProviderCredentials Credentials =
        new(CoolifyCredentialStorage.Serialize(InstanceUrl, "coolify-token"));

    /// <summary>
    /// The shape Coolify returns for a database it has published a port for. The host in
    /// <c>postgres_host</c> is a Docker-network name and resolves nowhere else, which is the whole
    /// difficulty: the field that looks like a host is the one that cannot be dialled.
    /// </summary>
    private const string PublicDatabase = """
        {
          "uuid": "db-1",
          "postgres_db": "yemenhub",
          "postgres_user": "yemenhub",
          "postgres_password": "s3cret",
          "postgres_host": "pavppdjqfvd4bray8uigiuo2",
          "postgres_port": 5432,
          "internal_db_url": "postgres://yemenhub:s3cret@pavppdjqfvd4bray8uigiuo2:5432/yemenhub",
          "is_public": true,
          "public_port": 54321
        }
        """;

    private const string PrivateDatabase = """
        {
          "uuid": "db-1",
          "postgres_db": "yemenhub",
          "postgres_user": "yemenhub",
          "postgres_password": "s3cret",
          "postgres_host": "pavppdjqfvd4bray8uigiuo2",
          "postgres_port": 5432,
          "internal_db_url": "postgres://yemenhub:s3cret@pavppdjqfvd4bray8uigiuo2:5432/yemenhub",
          "is_public": false,
          "public_port": null
        }
        """;

    [Fact]
    public async Task UsesTheInstanceHostAndPublishedPort_NotTheDockerHostname()
    {
        // The correction that makes this work at all: connect to the machine Coolify runs on at the
        // port it published, never to postgres_host, which only resolves inside the Docker network.
        var details = await ConnectionDetails(PublicDatabase);

        Assert.NotNull(details);
        Assert.Equal("46.225.80.188", details!.Host);
        Assert.Equal(54321, details.Port);
        Assert.Equal("yemenhub", details.Database);
        Assert.Equal("yemenhub", details.Username);
        Assert.Equal("s3cret", details.Password);
    }

    [Fact]
    public async Task ReturnsNothingWhenTheDatabaseIsNotPublished()
    {
        // Not an error and not a retry-later: a database Coolify has not published is unreachable
        // from outside by design. The caller turns this into a sentence saying so.
        Assert.Null(await ConnectionDetails(PrivateDatabase));
    }

    [Fact]
    public async Task ReturnsNothingWhenCredentialsAreIncomplete()
    {
        Assert.Null(await ConnectionDetails("""
            { "uuid": "db-1", "postgres_db": "yemenhub", "is_public": true, "public_port": 54321 }
            """));
    }

    [Fact]
    public async Task MetadataReportsTheInternalHostTheAppActuallyUses()
    {
        // The opposite question to the one above: "where does this live" is honestly answered by
        // the internal name, because that is what the application connects to.
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases/db-1")
            .Respond(HttpStatusCode.OK, "application/json", PrivateDatabase);

        var metadata = await new CoolifyProvider(handler.ToHttpClient())
            .GetMetadataAsync(Credentials, "db-1|env-1", "postgres", CancellationToken.None);

        Assert.Equal("postgres", metadata.Engine);
        Assert.Equal("yemenhub", metadata.DatabaseName);
        Assert.Equal("pavppdjqfvd4bray8uigiuo2", metadata.Host);
        Assert.Equal(5432, metadata.Port);
    }

    [Fact]
    public async Task AcceptsTheCompositeIdDeployAiStoresForADatabaseTarget()
    {
        // Database targets are stored as "{serviceId}|{environmentId}"; only the first half is the
        // database uuid, and requesting the whole string 404s.
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases/db-1")
            .Respond(HttpStatusCode.OK, "application/json", PublicDatabase);

        var details = await new CoolifyProvider(handler.ToHttpClient())
            .GetPostgresConnectionDetailsAsync(Credentials, "db-1|env-1", CancellationToken.None);

        Assert.NotNull(details);
    }

    [Fact]
    public void ResolvesTheHostFromTheInstanceUrl()
    {
        Assert.Equal("46.225.80.188", CoolifyProvider.ResolveInstanceHost("http://46.225.80.188:8000"));
        Assert.Equal("coolify.example.com", CoolifyProvider.ResolveInstanceHost("https://coolify.example.com"));
        Assert.Null(CoolifyProvider.ResolveInstanceHost("not-a-url"));
    }

    private static async Task<PostgresConnectionDetails?> ConnectionDetails(string databaseJson)
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases/db-1")
            .Respond(HttpStatusCode.OK, "application/json", databaseJson);

        return await new CoolifyProvider(handler.ToHttpClient())
            .GetPostgresConnectionDetailsAsync(Credentials, "db-1", CancellationToken.None);
    }
}
