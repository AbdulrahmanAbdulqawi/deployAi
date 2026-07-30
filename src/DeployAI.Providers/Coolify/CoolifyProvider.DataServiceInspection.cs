using DeployAI.Core.Providers;

namespace DeployAI.Providers.Coolify;

/// <summary>
/// Reads a Coolify-provisioned database's identity, and its connection details when Coolify has
/// been asked to expose it.
/// </summary>
/// <remarks>
/// This capability was Railway-only, so every Coolify database — the default — answered
/// <c>unsupported_provider</c>: DeployAI provisioned a database and then could not look at it, which
/// is why "did the migrations actually apply" had no answer on the provider it defaults to.
/// <para>
/// A Coolify database is reachable in two different ways and only one of them is DeployAI's to use.
/// The connection string written onto the app is Coolify's <c>internal_db_url</c>, a Docker-network
/// hostname that resolves only inside the host — correct for the app, useless from here. A database
/// is reachable from outside only when someone has turned on Coolify's public access, which
/// publishes a port on the instance's own host. So this reports what it can see rather than
/// pretending: connection details when the database is public, and null when it is not, so the
/// caller can say which of those it is instead of "not available yet".
/// </para>
/// <para>
/// Turning public access on is deliberately not done here. Exposing a database to the internet is a
/// decision with consequences a deploy step must not make on someone's behalf.
/// </para>
/// </remarks>
public sealed partial class CoolifyProvider : IProviderDataServiceInspection
{
    public async Task<DataServiceMetadata> GetMetadataAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string databaseEngine,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var databaseUuid = ParseDatabaseUuid(providerProjectId);
        var database = await GetDatabaseAsync(session, databaseUuid, cancellationToken);
        var isRedis = string.Equals(databaseEngine, "redis", StringComparison.OrdinalIgnoreCase);

        // The internal hostname is what the app connects on, so it is the honest answer for "where
        // does this live" even though nothing outside the Docker network can dial it.
        var host = isRedis ? database?.RedisHost : database?.PostgresHost;
        var port = isRedis ? database?.RedisPort ?? 6379 : database?.PostgresPort ?? 5432;

        return new DataServiceMetadata(
            databaseEngine,
            isRedis ? null : database?.PostgresDb,
            host,
            port,
            VolumeMountPath: null,
            RailwayServiceId: null,
            RailwayProjectId: null,
            RailwayEnvironmentId: null);
    }

    public async Task<PostgresConnectionDetails?> GetPostgresConnectionDetailsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var databaseUuid = ParseDatabaseUuid(providerProjectId);
        var database = await GetDatabaseAsync(session, databaseUuid, cancellationToken);

        if (database is null ||
            string.IsNullOrWhiteSpace(database.PostgresDb) ||
            string.IsNullOrWhiteSpace(database.PostgresUser) ||
            string.IsNullOrWhiteSpace(database.PostgresPassword))
        {
            return null;
        }

        // Only a published port is reachable from wherever DeployAI runs. Coolify exposes it on the
        // instance's own host, which is the host of the API being spoken to.
        if (database.IsPublic != true || database.PublicPort is not { } publicPort)
        {
            return null;
        }

        var host = ResolveInstanceHost(session.InstanceUrl);
        if (host is null)
        {
            return null;
        }

        return new PostgresConnectionDetails(
            host,
            publicPort,
            database.PostgresDb!,
            database.PostgresUser!,
            database.PostgresPassword!);
    }

    /// <summary>
    /// The machine Coolify itself runs on, taken from the API URL. A published database port is
    /// opened on that host, not on the Docker-internal name the app uses.
    /// </summary>
    internal static string? ResolveInstanceHost(string instanceUrl) =>
        Uri.TryCreate(instanceUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
