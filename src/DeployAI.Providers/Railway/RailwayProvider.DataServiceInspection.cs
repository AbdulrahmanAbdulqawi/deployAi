using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider : IProviderDataServiceInspection
{
    public async Task<DataServiceMetadata> GetMetadataAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string databaseEngine,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);
        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);
        var expectedMountPath = string.Equals(databaseEngine, "redis", StringComparison.OrdinalIgnoreCase)
            ? RedisDataMountPath
            : PostgresDataMountPath;

        string? databaseName = null;
        string? host = null;
        int? port = null;

        if (string.Equals(databaseEngine, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            databaseName = await GetServiceVariableValueAsync(
                credentials,
                projectId,
                environmentId,
                serviceId,
                "POSTGRES_DB",
                cancellationToken);
            host = await GetServiceVariableValueAsync(
                credentials,
                projectId,
                environmentId,
                serviceId,
                "RAILWAY_PRIVATE_DOMAIN",
                cancellationToken);
            port = 5432;
        }
        else if (string.Equals(databaseEngine, "redis", StringComparison.OrdinalIgnoreCase))
        {
            host = await GetServiceVariableValueAsync(
                credentials,
                projectId,
                environmentId,
                serviceId,
                "RAILWAY_PRIVATE_DOMAIN",
                cancellationToken);
            port = 6379;
        }

        var volumes = await ListVolumeInstancesAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            cancellationToken);
        var volumeMountPath = volumes
            .Select(volume => volume.MountPath)
            .FirstOrDefault(mountPath => !string.IsNullOrWhiteSpace(mountPath))
            ?? expectedMountPath;

        return new DataServiceMetadata(
            databaseEngine,
            databaseName,
            host,
            port,
            volumeMountPath,
            serviceId,
            projectId,
            environmentId);
    }

    public async Task<PostgresConnectionDetails?> GetPostgresConnectionDetailsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);
        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);

        var host = await GetServiceVariableValueAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            "RAILWAY_PRIVATE_DOMAIN",
            cancellationToken);
        var database = await GetServiceVariableValueAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            "POSTGRES_DB",
            cancellationToken);
        var username = await GetServiceVariableValueAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            "POSTGRES_USER",
            cancellationToken);
        var password = await GetServiceVariableValueAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            "POSTGRES_PASSWORD",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(database) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return new PostgresConnectionDetails(
            host,
            5432,
            database,
            username,
            password);
    }
}
