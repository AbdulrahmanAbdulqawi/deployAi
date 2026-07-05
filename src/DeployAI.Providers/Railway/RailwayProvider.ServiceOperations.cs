using System.Text.Json;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider : IProviderServiceOperations
{
    public async Task<ProviderServiceStatus> GetServiceStatusAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);

        const string query = """
            query ServiceStatus($serviceId: String!, $environmentId: String!) {
              service(id: $serviceId) {
                serviceInstances {
                  edges {
                    node {
                      environmentId
                      latestDeployment {
                        status
                        url
                        createdAt
                      }
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
            new { serviceId, environmentId },
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("service", out var serviceNode))
        {
            return new ProviderServiceStatus("unknown", null, null);
        }

        if (!serviceNode.TryGetProperty("serviceInstances", out var instancesNode) ||
            !instancesNode.TryGetProperty("edges", out var edges) ||
            edges.ValueKind != JsonValueKind.Array)
        {
            return new ProviderServiceStatus("unknown", null, null);
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

            if (!node.TryGetProperty("latestDeployment", out var deploymentNode) ||
                deploymentNode.ValueKind == JsonValueKind.Null)
            {
                return new ProviderServiceStatus("not_deployed", null, null);
            }

            var status = deploymentNode.TryGetProperty("status", out var statusNode)
                ? statusNode.GetString()?.ToLowerInvariant() ?? "unknown"
                : "unknown";
            var url = deploymentNode.TryGetProperty("url", out var urlNode) ? urlNode.GetString() : null;
            DateTimeOffset? lastDeployedAt = null;
            if (deploymentNode.TryGetProperty("createdAt", out var createdAtNode) &&
                createdAtNode.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(createdAtNode.GetString(), out var parsed))
            {
                lastDeployedAt = parsed;
            }

            return new ProviderServiceStatus(MapServiceStatus(status), url, lastDeployedAt);
        }

        return new ProviderServiceStatus("unknown", null, null);
    }

    public Task RedeployServiceAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);
        return RedeployServiceInstanceAsync(credentials, serviceId, environmentId, cancellationToken);
    }

    public async Task DeleteServiceAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);
        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);
        var volumes = await ListVolumeInstancesAsync(
            credentials,
            projectId,
            environmentId,
            serviceId,
            cancellationToken);

        foreach (var volume in volumes)
        {
            await DeleteVolumeAsync(credentials, volume.VolumeId, cancellationToken);
        }

        const string mutation = """
            mutation DeleteService($serviceId: String!) {
              serviceDelete(id: $serviceId)
            }
            """;

        await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            mutation,
            new { serviceId },
            cancellationToken);
    }

    private async Task DeleteVolumeAsync(
        ProviderCredentials credentials,
        string volumeId,
        CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation DeleteVolume($volumeId: String!) {
              volumeDelete(volumeId: $volumeId)
            }
            """;

        await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            mutation,
            new { volumeId },
            cancellationToken);
    }

    private static string MapServiceStatus(string status) =>
        status switch
        {
            "success" or "succeeded" or "active" or "running" => "running",
            "failed" or "crashed" or "removed" => "failed",
            "building" or "deploying" or "initializing" or "queued" => "deploying",
            _ => status
        };
}
