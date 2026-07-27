namespace DeployAI.Core.Providers;

/// <summary>A database service created on a provider as a side effect of app provisioning.</summary>
public sealed record ProvisionedDatabaseService(
    string ServiceId,
    string ServiceName,
    string ProjectId,
    string EnvironmentId);

/// <summary>Which database engines to provision, and an optional preferred database name.</summary>
public sealed record DatabaseProvisioningRequest(
    bool IncludePostgres,
    bool IncludeRedis,
    string? PostgresDatabaseName = null);

/// <summary>An env var key on the app service that should reference a provisioned database's connection info.</summary>
public sealed record DatabaseVariableLink(
    string Key,
    string ReferenceValue);

/// <summary>
/// Optional capability for providers that can provision Postgres/Redis alongside an app (Railway,
/// Coolify) - resolved via <see cref="IProviderDatabaseProvisioningFactory"/> since Vercel doesn't
/// implement it.
/// </summary>
public interface IProviderDatabaseProvisioning
{
    string ProviderName { get; }

    /// <summary>Creates a Postgres database service if one doesn't already exist for the app, otherwise returns the existing one.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="appProviderProjectId">The app/service to provision the database alongside.</param>
    /// <param name="postgresDatabaseName">Preferred database name, if the repo specifies one.</param>
    Task<ProvisionedDatabaseService?> EnsurePostgresAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        string? postgresDatabaseName,
        CancellationToken cancellationToken);

    /// <summary>Creates a Redis service if one doesn't already exist for the app, otherwise returns the existing one.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="appProviderProjectId">The app/service to provision the database alongside.</param>
    Task<ProvisionedDatabaseService?> EnsureRedisAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        CancellationToken cancellationToken);

    /// <summary>Wires the given connection-string env vars onto the app service, linked to the provisioned database.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="appProviderProjectId">The app/service to set env vars on.</param>
    /// <param name="links">The env var keys and their reference values to set.</param>
    Task LinkDatabaseVariablesAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        IReadOnlyList<DatabaseVariableLink> links,
        CancellationToken cancellationToken);

    /// <summary>Deletes a provisioned database service.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="databaseProviderProjectId">The database service to delete.</param>
    Task DeleteDatabaseAsync(
        ProviderCredentials credentials,
        string databaseProviderProjectId,
        CancellationToken cancellationToken);
}

/// <summary>Resolves the optional <see cref="IProviderDatabaseProvisioning"/> capability for a provider, if it supports one.</summary>
public interface IProviderDatabaseProvisioningFactory
{
    IProviderDatabaseProvisioning? GetProvisioning(string providerName);
}
