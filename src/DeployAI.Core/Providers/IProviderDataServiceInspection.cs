namespace DeployAI.Core.Providers;

/// <summary>Identifying/connection metadata for a provisioned data service (Postgres or Redis).</summary>
public sealed record DataServiceMetadata(
    string Engine,
    string? DatabaseName,
    string? Host,
    int? Port,
    string? VolumeMountPath,
    string? RailwayServiceId,
    string? RailwayProjectId,
    string? RailwayEnvironmentId);

/// <summary>Raw Postgres connection parameters, for connecting directly to inspect a database.</summary>
public sealed record PostgresConnectionDetails(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password);

/// <summary>A table name and its current row count.</summary>
public sealed record DatabaseTableInfo(string Name, long RowCount);

/// <summary>The result of connecting to and inspecting a provisioned Postgres database.</summary>
public sealed record PostgresDatabaseInspection(
    bool Connected,
    string? Message,
    IReadOnlyList<DatabaseTableInfo> Tables,
    IReadOnlyList<string> MigrationsApplied);

/// <summary>
/// Optional capability for providers whose data services can be inspected directly (metadata,
/// live Postgres connection details) - used to power the "data info" view for a database target.
/// </summary>
public interface IProviderDataServiceInspection
{
    string ProviderName { get; }

    /// <summary>Gets identifying metadata for a data service.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side data service id.</param>
    /// <param name="databaseEngine">"postgres" or "redis".</param>
    Task<DataServiceMetadata> GetMetadataAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string databaseEngine,
        CancellationToken cancellationToken);

    /// <summary>Gets raw connection details for a Postgres data service, if available.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side data service id.</param>
    Task<PostgresConnectionDetails?> GetPostgresConnectionDetailsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);
}

/// <summary>Resolves the optional <see cref="IProviderDataServiceInspection"/> capability for a provider, if it supports one.</summary>
public interface IProviderDataServiceInspectionFactory
{
    IProviderDataServiceInspection? GetInspection(string providerName);
}
