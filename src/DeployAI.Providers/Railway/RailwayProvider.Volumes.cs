using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;

namespace DeployAI.Providers.Railway;

/// <summary>Ensures a database service has a persistent volume mounted at the correct path (fixing a wrong mount path in place rather than creating a duplicate volume).</summary>
public sealed partial class RailwayProvider
{
    internal const string PostgresDataMountPath = "/var/lib/postgresql/data";
    internal const string RedisDataMountPath = "/data";

    private async Task<bool> EnsurePostgresVolumeAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string serviceId,
        CancellationToken cancellationToken) =>
        await EnsureServiceVolumeAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            PostgresDataMountPath,
            cancellationToken);

    private async Task<bool> EnsureRedisVolumeAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string serviceId,
        CancellationToken cancellationToken) =>
        await EnsureServiceVolumeAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            RedisDataMountPath,
            cancellationToken);

    /// <summary>Ensures a volume mounted at <paramref name="mountPath"/> exists on the service: no-op if one already matches, fixes an existing volume's mount path if one is wrong, else creates a new one.</summary>
    private async Task<bool> EnsureServiceVolumeAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string serviceId,
        string mountPath,
        CancellationToken cancellationToken)
    {
        var instances = await ListVolumeInstancesAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            cancellationToken);

        var matching = instances
            .Where(instance => string.Equals(instance.MountPath, mountPath, StringComparison.Ordinal))
            .ToList();

        if (matching.Count > 0)
        {
            return false;
        }

        var fixable = instances.FirstOrDefault(instance =>
            string.IsNullOrWhiteSpace(instance.MountPath) ||
            !string.Equals(instance.MountPath, mountPath, StringComparison.Ordinal));

        if (fixable is not null)
        {
            await UpdateVolumeInstanceMountPathAsync(
                credentials,
                fixable.VolumeId,
                environmentId,
                mountPath,
                cancellationToken);
            return true;
        }

        var region = await GetServiceInstanceRegionAsync(
            credentials,
            serviceId,
            environmentId,
            cancellationToken);

        await CreateVolumeAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            mountPath,
            region,
            cancellationToken);

        return true;
    }

    private async Task<IReadOnlyList<VolumeInstanceInfo>> ListVolumeInstancesAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string serviceId,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.EnvironmentVolumeInstances.ExecuteAsync(projectId, environmentId, cancellationToken);
        var data = RailwayApiSupport.TryGetData(result, static _ => false);
        if (data?.Environment?.VolumeInstances?.Edges is null)
        {
            return [];
        }

        var results = new List<VolumeInstanceInfo>();
        foreach (var edge in data.Environment.VolumeInstances.Edges)
        {
            var node = edge.Node;
            if (node is null ||
                string.IsNullOrWhiteSpace(node.Id) ||
                string.IsNullOrWhiteSpace(node.VolumeId) ||
                !string.Equals(node.ServiceId, serviceId, StringComparison.Ordinal))
            {
                continue;
            }

            results.Add(new VolumeInstanceInfo(node.Id, node.VolumeId, node.MountPath));
        }

        return results;
    }

    private async Task CreateVolumeAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string serviceId,
        string mountPath,
        string? region,
        CancellationToken cancellationToken)
    {
        var input = new VolumeCreateInput
        {
            ProjectId = projectId,
            EnvironmentId = environmentId,
            ServiceId = serviceId,
            MountPath = mountPath,
            Region = region
        };

        try
        {
            await using var gql = _graphQl.CreateSession(credentials);
            var result = await gql.Client.VolumeCreate.ExecuteAsync(input, cancellationToken);
            RailwayApiSupport.EnsureSuccess(result);
        }
        catch (DeployAIException ex) when (RailwayApiSupport.IsDuplicateVolumeError(ex.Message))
        {
            // Another request may have created the volume; caller will verify mount path.
        }
    }

    private async Task UpdateVolumeInstanceMountPathAsync(
        ProviderCredentials credentials,
        string volumeId,
        string environmentId,
        string mountPath,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.VolumeInstanceUpdate.ExecuteAsync(
            volumeId,
            environmentId,
            new VolumeInstanceUpdateInput { MountPath = mountPath },
            cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
    }

    private async Task<string?> GetServiceInstanceRegionAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.ServiceInstanceRegion.ExecuteAsync(serviceId, cancellationToken);
        var data = RailwayApiSupport.TryGetData(result, static _ => false);
        if (data?.Service?.ServiceInstances?.Edges is null)
        {
            return null;
        }

        foreach (var edge in data.Service.ServiceInstances.Edges)
        {
            var node = edge.Node;
            if (node is null)
            {
                continue;
            }

            if (!string.Equals(node.EnvironmentId, environmentId, StringComparison.Ordinal))
            {
                continue;
            }

            return node.Region;
        }

        return null;
    }

    private sealed record VolumeInstanceInfo(string Id, string VolumeId, string? MountPath);
}
