using System.Security.Cryptography;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

/// <summary>
/// Private helpers for <c>RailwayProvider.Database.cs</c>: idempotently ensures the env vars a
/// provisioned Postgres/Redis service needs to actually work (credentials, connection strings,
/// data directory), only touching vars that are missing or wrong so re-running is a no-op.
/// </summary>
public sealed partial class RailwayProvider
{
    private async Task<bool> EnsurePostgresPluginVariablesAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        string? postgresDatabaseName,
        CancellationToken cancellationToken)
    {
        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);
        var databaseName = NormalizePostgresDatabaseName(postgresDatabaseName);
        var hasPassword = await ServiceHasVariableAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            "POSTGRES_PASSWORD",
            cancellationToken);
        var hasDatabaseUrl = await ServiceHasVariableAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            "DATABASE_URL",
            cancellationToken);

        if (hasPassword && hasDatabaseUrl)
        {
            var configChanged = false;
            var currentDbName = await GetServiceVariableValueAsync(
                credentials,
                projectId,
                environmentId,
                serviceId,
                "POSTGRES_DB",
                cancellationToken);

            if (string.IsNullOrWhiteSpace(currentDbName))
            {
                await EnsureServiceVariableAsync(
                    credentials,
                    serviceId,
                    environmentId,
                    "POSTGRES_DB",
                    databaseName,
                    cancellationToken);
                configChanged = true;
            }
            else if (!string.Equals(currentDbName, databaseName, StringComparison.Ordinal))
            {
                throw new DeployAIException(
                    "railway_postgres_database_mismatch",
                    $"PostgreSQL was already initialized with database \"{currentDbName}\". Remove the Postgres service from DeployAI and add it again to use \"{databaseName}\".");
            }

            configChanged |= await EnsurePostgresDataDirectoryAsync(
                credentials,
                serviceId,
                environmentId,
                cancellationToken);
            return configChanged;
        }

        var password = GenerateSecretToken();
        await EnsureServiceVariableAsync(credentials, serviceId, environmentId, "POSTGRES_USER", "postgres", cancellationToken);
        await EnsureServiceVariableAsync(credentials, serviceId, environmentId, "POSTGRES_PASSWORD", password, cancellationToken);
        await EnsureServiceVariableAsync(credentials, serviceId, environmentId, "POSTGRES_DB", databaseName, cancellationToken);
        await EnsureServiceVariableAsync(
            credentials,
            serviceId,
            environmentId,
            "DATABASE_URL",
            "postgresql://${{POSTGRES_USER}}:${{POSTGRES_PASSWORD}}@${{RAILWAY_PRIVATE_DOMAIN}}:5432/${{POSTGRES_DB}}",
            cancellationToken);

        await EnsurePostgresDataDirectoryAsync(
            credentials,
            serviceId,
            environmentId,
            cancellationToken);
        return true;
    }

    private async Task<bool> EnsurePostgresDataDirectoryAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        CancellationToken cancellationToken)
    {
        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);
        const string pgDataPath = "/var/lib/postgresql/data/pgdata";
        var currentPgData = await GetServiceVariableValueAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            "PGDATA",
            cancellationToken);
        if (string.Equals(currentPgData, pgDataPath, StringComparison.Ordinal))
        {
            return false;
        }

        await EnsureServiceVariableAsync(
            credentials,
            serviceId,
            environmentId,
            "PGDATA",
            pgDataPath,
            cancellationToken);
        return true;
    }

    private async Task<bool> EnsureRedisPluginVariablesAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        CancellationToken cancellationToken)
    {
        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);
        if (await ServiceHasVariableAsync(credentials, projectId, environmentId, serviceId, "REDIS_URL", cancellationToken))
        {
            return false;
        }

        await EnsureServiceVariableAsync(
            credentials,
            serviceId,
            environmentId,
            "REDIS_URL",
            "redis://${{RAILWAY_PRIVATE_DOMAIN}}:6379",
            cancellationToken);

        return true;
    }

    private static string NormalizePostgresDatabaseName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "railway" : name.Trim();

    private async Task<bool> ServiceHasVariableAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string serviceId,
        string name,
        CancellationToken cancellationToken)
    {
        var value = await GetServiceVariableValueAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            name,
            cancellationToken);
        return !string.IsNullOrWhiteSpace(value);
    }

    private async Task<string?> GetServiceVariableValueAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string serviceId,
        string name,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.GetVariables.ExecuteAsync(projectId, environmentId, serviceId, cancellationToken);
        var data = RailwayApiSupport.TryGetData(result, static _ => false);
        var variables = RailwayGraphQlMapping.ParseVariablesJson(data?.Variables);
        return variables.TryGetValue(name, out var value) ? value : null;
    }

    private async Task RedeployServiceInstanceAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        CancellationToken cancellationToken) =>
        await RedeployServiceInstanceAsync(credentials, serviceId, environmentId, ensurePublicDomain: false, cancellationToken);

    private async Task RedeployServiceInstanceAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        bool ensurePublicDomain,
        CancellationToken cancellationToken)
    {
        if (ensurePublicDomain)
        {
            await EnsurePublicServiceDomainAsync(credentials, serviceId, environmentId, environment: null, cancellationToken);
        }

        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.RedeployService.ExecuteAsync(serviceId, environmentId, cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
    }

    private static string GenerateSecretToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
