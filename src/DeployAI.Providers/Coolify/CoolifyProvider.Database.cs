using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Coolify;

public sealed partial class CoolifyProvider : IProviderDatabaseProvisioning, IProviderApplicationUrlResolver
{
    public async Task<ProvisionedDatabaseService?> EnsurePostgresAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        string? postgresDatabaseName,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var application = await GetApplicationContextAsync(session, appProviderProjectId, cancellationToken);
        if (application is null)
        {
            return null;
        }

        var existing = await FindExistingPostgresAsync(session, application, postgresDatabaseName, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var databaseName = string.IsNullOrWhiteSpace(postgresDatabaseName)
            ? $"{SanitizeResourceName(application.Name)}-postgres"
            : SanitizeResourceName(postgresDatabaseName);

        var body = new Dictionary<string, object?>
        {
            ["server_uuid"] = application.ServerUuid,
            ["project_uuid"] = application.ProjectUuid,
            ["environment_name"] = application.EnvironmentName,
            ["environment_uuid"] = application.EnvironmentUuid,
            ["name"] = databaseName,
            ["postgres_db"] = databaseName.Replace('-', '_'),
            ["postgres_user"] = databaseName.Replace('-', '_'),
            ["instant_deploy"] = true
        };

        using var request = CreateRequest(HttpMethod.Post, session, "databases/postgresql");
        request.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not create PostgreSQL on Coolify ({(int)response.StatusCode}).");
        }

        var created = await response.Content.ReadFromJsonAsync<CoolifyUuidResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(created?.Uuid))
        {
            throw new DeployAIException("coolify_api_error", "Coolify did not return a database id.");
        }

        return new ProvisionedDatabaseService(
            created.Uuid,
            databaseName,
            application.ProjectUuid,
            application.EnvironmentUuid);
    }

    public Task<ProvisionedDatabaseService?> EnsureRedisAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        CancellationToken cancellationToken)
    {
        return EnsureRedisInternalAsync(credentials, appProviderProjectId, cancellationToken);
    }

    public async Task DeleteDatabaseAsync(
        ProviderCredentials credentials,
        string databaseProviderProjectId,
        CancellationToken cancellationToken)
    {
        var databaseUuid = ParseDatabaseUuid(databaseProviderProjectId);
        var session = CoolifyApiSupport.ParseSession(credentials);
        using var request = CreateRequest(HttpMethod.Delete, session, $"databases/{databaseUuid}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not delete Coolify database ({(int)response.StatusCode}).");
        }
    }

    private async Task<ProvisionedDatabaseService?> EnsureRedisInternalAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var application = await GetApplicationContextAsync(session, appProviderProjectId, cancellationToken);
        if (application is null)
        {
            return null;
        }

        var existing = await FindExistingRedisAsync(session, application, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var databaseName = $"{SanitizeResourceName(application.Name)}-redis";
        var body = new Dictionary<string, object?>
        {
            ["server_uuid"] = application.ServerUuid,
            ["project_uuid"] = application.ProjectUuid,
            ["environment_name"] = application.EnvironmentName,
            ["environment_uuid"] = application.EnvironmentUuid,
            ["name"] = databaseName,
            ["instant_deploy"] = true
        };

        using var createRequest = CreateRequest(HttpMethod.Post, session, "databases/redis");
        createRequest.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(createRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not create Redis on Coolify ({(int)response.StatusCode}).");
        }

        var created = await response.Content.ReadFromJsonAsync<CoolifyUuidResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(created?.Uuid))
        {
            throw new DeployAIException("coolify_api_error", "Coolify did not return a database id.");
        }

        return new ProvisionedDatabaseService(
            created.Uuid,
            databaseName,
            application.ProjectUuid,
            application.EnvironmentUuid);
    }

    public async Task LinkDatabaseVariablesAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        IReadOnlyList<DatabaseVariableLink> links,
        CancellationToken cancellationToken)
    {
        if (links.Count == 0)
        {
            return;
        }

        var session = CoolifyApiSupport.ParseSession(credentials);
        foreach (var link in links)
        {
            var database = await GetDatabaseAsync(session, link.ReferenceValue, cancellationToken);
            var connectionString = ResolvePostgresConnectionString(database)
                ?? ResolveRedisConnectionString(database);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                continue;
            }

            await UpsertEnvVarAsync(
                credentials,
                appProviderProjectId,
                new UpsertProviderEnvVarRequest(link.Key, connectionString, "plain", []),
                cancellationToken);

            if (string.Equals(link.Key, "DATABASE_URL", StringComparison.OrdinalIgnoreCase))
            {
                await UpsertEnvVarAsync(
                    credentials,
                    appProviderProjectId,
                    new UpsertProviderEnvVarRequest("POSTGRES_URL", connectionString, "plain", []),
                    cancellationToken);
            }
        }
    }

    /// <summary>Strips a trailing "|env" suffix (if present) to get the raw Coolify database uuid.</summary>
    private static string ParseDatabaseUuid(string databaseProviderProjectId)
    {
        var separatorIndex = databaseProviderProjectId.IndexOf('|', StringComparison.Ordinal);
        return separatorIndex < 0
            ? databaseProviderProjectId
            : databaseProviderProjectId[..separatorIndex];
    }

    /// <summary>Builds a Redis connection URL from Coolify's database details, preferring the internal Docker URL if Coolify returned one.</summary>
    private static string? ResolveRedisConnectionString(CoolifyDatabaseDetails? database)
    {
        if (database is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(database.InternalDbUrl))
        {
            return database.InternalDbUrl;
        }

        if (!string.IsNullOrWhiteSpace(database.RedisPassword) &&
            !string.IsNullOrWhiteSpace(database.RedisHost))
        {
            var port = database.RedisPort ?? 6379;
            return $"redis://:{database.RedisPassword}@{database.RedisHost}:{port}";
        }

        return null;
    }

    /// <summary>Builds a Postgres connection URL from Coolify's database details, preferring Coolify's own connection string field, then internal Docker URL, then assembling from individual fields.</summary>
    private static string? ResolvePostgresConnectionString(CoolifyDatabaseDetails? database)
    {
        if (database is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(database.PostgresConnectionString))
        {
            return database.PostgresConnectionString;
        }

        if (!string.IsNullOrWhiteSpace(database.InternalDbUrl))
        {
            return database.InternalDbUrl;
        }

        if (string.IsNullOrWhiteSpace(database.PostgresUser) ||
            string.IsNullOrWhiteSpace(database.PostgresPassword) ||
            string.IsNullOrWhiteSpace(database.PostgresHost) ||
            string.IsNullOrWhiteSpace(database.PostgresDb))
        {
            return null;
        }

        var port = database.PostgresPort ?? 5432;
        return $"postgres://{database.PostgresUser}:{database.PostgresPassword}@{database.PostgresHost}:{port}/{database.PostgresDb}";
    }

    public async Task<string?> ResolveApplicationUrlAsync(
        ProviderCredentials credentials,
        string applicationUuid,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var application = await TryGetApplicationAsync(session, applicationUuid, cancellationToken);
        return NormalizeUrl(application?.Fqdn);
    }

    private async Task<ProvisionedDatabaseService?> FindExistingPostgresAsync(
        CoolifyApiSupport.CoolifySession session,
        CoolifyApplicationContext application,
        string? postgresDatabaseName,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, "databases");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var databases = await response.Content.ReadFromJsonAsync<List<CoolifyDatabaseSummary>>(cancellationToken) ?? [];
        var desiredName = string.IsNullOrWhiteSpace(postgresDatabaseName)
            ? $"{SanitizeResourceName(application.Name)}-postgres"
            : SanitizeResourceName(postgresDatabaseName);

        var match = databases.FirstOrDefault(database =>
            string.Equals(database.Type, "postgresql", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(database.Name, desiredName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(database.Uuid, desiredName, StringComparison.OrdinalIgnoreCase)));

        return match?.Uuid is { Length: > 0 } uuid
            ? new ProvisionedDatabaseService(uuid, match.Name ?? desiredName, application.ProjectUuid, application.EnvironmentUuid)
            : null;
    }

    private async Task<ProvisionedDatabaseService?> FindExistingRedisAsync(
        CoolifyApiSupport.CoolifySession session,
        CoolifyApplicationContext application,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, "databases");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var databases = await response.Content.ReadFromJsonAsync<List<CoolifyDatabaseSummary>>(cancellationToken) ?? [];
        var desiredName = $"{SanitizeResourceName(application.Name)}-redis";

        var match = databases.FirstOrDefault(database =>
            string.Equals(database.Type, "redis", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(database.Name, desiredName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(database.Uuid, desiredName, StringComparison.OrdinalIgnoreCase)));

        return match?.Uuid is { Length: > 0 } uuid
            ? new ProvisionedDatabaseService(uuid, match.Name ?? desiredName, application.ProjectUuid, application.EnvironmentUuid)
            : null;
    }

    /// <summary>Fetches an application's project/server/environment context needed to provision a database alongside it, defaulting the environment to "production" if Coolify doesn't report one.</summary>
    private async Task<CoolifyApplicationContext?> GetApplicationContextAsync(
        CoolifyApiSupport.CoolifySession session,
        string applicationUuid,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, $"applications/{applicationUuid}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var application = await response.Content.ReadFromJsonAsync<CoolifyApplicationContext>(cancellationToken);
        if (application is null ||
            string.IsNullOrWhiteSpace(application.Uuid) ||
            string.IsNullOrWhiteSpace(application.ProjectUuid) ||
            string.IsNullOrWhiteSpace(application.ServerUuid))
        {
            return null;
        }

        application.EnvironmentName ??= "production";
        application.EnvironmentUuid ??= application.EnvironmentName;
        return application;
    }

    private async Task<CoolifyDatabaseDetails?> GetDatabaseAsync(
        CoolifyApiSupport.CoolifySession session,
        string databaseUuid,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, $"databases/{databaseUuid}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<CoolifyDatabaseDetails>(cancellationToken);
    }

    /// <summary>Lowercases and strips non-alphanumeric characters to a Coolify-safe resource name, falling back to "app-postgres" if nothing usable remains.</summary>
    private static string SanitizeResourceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "app-postgres";
        }

        var sanitized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');

        return string.IsNullOrWhiteSpace(sanitized) ? "app-postgres" : sanitized;
    }

    private sealed class CoolifyApplicationContext
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("project_uuid")]
        public string? ProjectUuid { get; set; }

        [JsonPropertyName("server_uuid")]
        public string? ServerUuid { get; set; }

        [JsonPropertyName("environment_name")]
        public string? EnvironmentName { get; set; }

        [JsonPropertyName("environment_uuid")]
        public string? EnvironmentUuid { get; set; }
    }

    private sealed class CoolifyDatabaseSummary
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    private sealed class CoolifyDatabaseDetails
    {
        [JsonPropertyName("postgres_connection_string")]
        public string? PostgresConnectionString { get; set; }

        [JsonPropertyName("internal_db_url")]
        public string? InternalDbUrl { get; set; }

        [JsonPropertyName("postgres_db")]
        public string? PostgresDb { get; set; }

        [JsonPropertyName("postgres_user")]
        public string? PostgresUser { get; set; }

        [JsonPropertyName("postgres_password")]
        public string? PostgresPassword { get; set; }

        [JsonPropertyName("postgres_host")]
        public string? PostgresHost { get; set; }

        [JsonPropertyName("postgres_port")]
        public int? PostgresPort { get; set; }

        [JsonPropertyName("redis_password")]
        public string? RedisPassword { get; set; }

        [JsonPropertyName("redis_host")]
        public string? RedisHost { get; set; }

        [JsonPropertyName("redis_port")]
        public int? RedisPort { get; set; }
    }
}
