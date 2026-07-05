using System.Text.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider
{
    internal const string PostgresDataMountPath = "/var/lib/postgresql/data";
    internal const string RedisDataMountPath = "/data";

    private async Task EnsurePostgresVolumeAsync(
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

    private async Task EnsureRedisVolumeAsync(
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

    private async Task EnsureServiceVolumeAsync(
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
            return;
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
            await RedeployServiceInstanceAsync(credentials, serviceId, environmentId, cancellationToken);
            return;
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

        await RedeployServiceInstanceAsync(credentials, serviceId, environmentId, cancellationToken);
    }

    private async Task<IReadOnlyList<VolumeInstanceInfo>> ListVolumeInstancesAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string serviceId,
        CancellationToken cancellationToken)
    {
        const string query = """
            query EnvironmentVolumeInstances($projectId: String!, $environmentId: String!) {
              environment(id: $environmentId, projectId: $projectId) {
                volumeInstances {
                  edges {
                    node {
                      id
                      volumeId
                      serviceId
                      mountPath
                    }
                  }
                }
              }
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { projectId, environmentId },
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("environment", out var environmentNode) ||
            !environmentNode.TryGetProperty("volumeInstances", out var instancesNode) ||
            !instancesNode.TryGetProperty("edges", out var edges) ||
            edges.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<VolumeInstanceInfo>();
        foreach (var edge in edges.EnumerateArray())
        {
            if (!edge.TryGetProperty("node", out var node))
            {
                continue;
            }

            var id = node.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
            var volumeId = node.TryGetProperty("volumeId", out var volumeIdNode) ? volumeIdNode.GetString() : null;
            var instanceServiceId = node.TryGetProperty("serviceId", out var serviceNode)
                ? serviceNode.GetString()
                : null;
            var mountPath = node.TryGetProperty("mountPath", out var mountNode) ? mountNode.GetString() : null;

            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(volumeId) ||
                !string.Equals(instanceServiceId, serviceId, StringComparison.Ordinal))
            {
                continue;
            }

            results.Add(new VolumeInstanceInfo(id, volumeId, mountPath));
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
        const string mutation = """
            mutation VolumeCreate($input: VolumeCreateInput!) {
              volumeCreate(input: $input) {
                id
                name
              }
            }
            """;

        var input = new Dictionary<string, object?>
        {
            ["projectId"] = projectId,
            ["environmentId"] = environmentId,
            ["serviceId"] = serviceId,
            ["mountPath"] = mountPath
        };

        if (!string.IsNullOrWhiteSpace(region))
        {
            input["region"] = region;
        }

        try
        {
            await RailwayApiSupport.ExecuteAsync(
                _httpClient,
                credentials.Token,
                mutation,
                new { input },
                cancellationToken);
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
        const string mutation = """
            mutation VolumeInstanceUpdate($volumeId: String!, $environmentId: String!, $input: VolumeInstanceUpdateInput!) {
              volumeInstanceUpdate(volumeId: $volumeId, environmentId: $environmentId, input: $input)
            }
            """;

        await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            mutation,
            new
            {
                volumeId,
                environmentId,
                input = new { mountPath }
            },
            cancellationToken);
    }

    private async Task<string?> GetServiceInstanceRegionAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        CancellationToken cancellationToken)
    {
        const string query = """
            query ServiceInstanceRegion($id: String!) {
              service(id: $id) {
                serviceInstances {
                  edges {
                    node {
                      environmentId
                      region
                    }
                  }
                }
              }
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { id = serviceId },
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("service", out var serviceNode) ||
            !serviceNode.TryGetProperty("serviceInstances", out var instancesNode) ||
            !instancesNode.TryGetProperty("edges", out var edges) ||
            edges.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var edge in edges.EnumerateArray())
        {
            if (!edge.TryGetProperty("node", out var node))
            {
                continue;
            }

            var instanceEnvironmentId = node.TryGetProperty("environmentId", out var envNode)
                ? envNode.GetString()
                : null;
            if (!string.Equals(instanceEnvironmentId, environmentId, StringComparison.Ordinal))
            {
                continue;
            }

            return node.TryGetProperty("region", out var regionNode) ? regionNode.GetString() : null;
        }

        return null;
    }

    private sealed record VolumeInstanceInfo(string Id, string VolumeId, string? MountPath);
}
