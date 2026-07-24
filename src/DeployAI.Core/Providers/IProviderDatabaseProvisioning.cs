namespace DeployAI.Core.Providers;

public sealed record ProvisionedDatabaseService(
    string ServiceId,
    string ServiceName,
    string ProjectId,
    string EnvironmentId);

public sealed record DatabaseProvisioningRequest(
    bool IncludePostgres,
    bool IncludeRedis,
    string? PostgresDatabaseName = null,
    // The ConnectionStrings:* keys the app actually reads (from appsettings), so the wired env
    // vars match — an app that names its connection "Postgres" won't read "Default".
    IReadOnlyList<string>? ConnectionStringKeys = null);

public sealed record DatabaseVariableLink(
    string Key,
    string ReferenceValue);

public interface IProviderDatabaseProvisioning
{
    string ProviderName { get; }

    Task<ProvisionedDatabaseService?> EnsurePostgresAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        string? postgresDatabaseName,
        CancellationToken cancellationToken);

    Task<ProvisionedDatabaseService?> EnsureRedisAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        CancellationToken cancellationToken);

    Task LinkDatabaseVariablesAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        IReadOnlyList<DatabaseVariableLink> links,
        CancellationToken cancellationToken);

    Task DeleteDatabaseAsync(
        ProviderCredentials credentials,
        string databaseProviderProjectId,
        CancellationToken cancellationToken);
}

public interface IProviderDatabaseProvisioningFactory
{
    IProviderDatabaseProvisioning? GetProvisioning(string providerName);
}
