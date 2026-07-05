namespace DeployAI.Core.Providers;

public sealed record ProvisionedDatabaseService(
    string ServiceId,
    string ServiceName,
    string ProjectId,
    string EnvironmentId);

public sealed record DatabaseProvisioningRequest(
    bool IncludePostgres,
    bool IncludeRedis,
    string? PostgresDatabaseName = null);

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
}

public interface IProviderDatabaseProvisioningFactory
{
    IProviderDatabaseProvisioning? GetProvisioning(string providerName);
}
