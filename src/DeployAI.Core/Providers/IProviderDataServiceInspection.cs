namespace DeployAI.Core.Providers;

public sealed record DataServiceMetadata(
    string Engine,
    string? DatabaseName,
    string? Host,
    int? Port,
    string? VolumeMountPath,
    string? RailwayServiceId,
    string? RailwayProjectId,
    string? RailwayEnvironmentId);

public sealed record PostgresConnectionDetails(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password);

public sealed record DatabaseTableInfo(string Name, long RowCount);

public sealed record PostgresDatabaseInspection(
    bool Connected,
    string? Message,
    IReadOnlyList<DatabaseTableInfo> Tables,
    IReadOnlyList<string> MigrationsApplied);

public interface IProviderDataServiceInspection
{
    string ProviderName { get; }

    Task<DataServiceMetadata> GetMetadataAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string databaseEngine,
        CancellationToken cancellationToken);

    Task<PostgresConnectionDetails?> GetPostgresConnectionDetailsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);
}

public interface IProviderDataServiceInspectionFactory
{
    IProviderDataServiceInspection? GetInspection(string providerName);
}
