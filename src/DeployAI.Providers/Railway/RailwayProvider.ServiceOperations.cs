using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider : IProviderServiceOperations
{
    /// <summary>Reads a service's latest deployment status/URL/timestamp for the environment encoded in <paramref name="providerProjectId"/>.</summary>
    public async Task<ProviderServiceStatus> GetServiceStatusAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);

        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.ServiceStatus.ExecuteAsync(serviceId, cancellationToken);
        var data = RailwayApiSupport.TryGetData(result, static _ => false);
        if (data?.Service?.ServiceInstances?.Edges is null)
        {
            return new ProviderServiceStatus("unknown", null, null);
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

            var deployment = node.LatestDeployment;
            if (deployment is null)
            {
                return new ProviderServiceStatus("not_deployed", null, null);
            }

            var status = RailwayGraphQlMapping.MapServiceDeploymentStatus(deployment.Status);
            var serviceDomains = node.Domains?.ServiceDomains?
                .Select(domain => new ServiceDomainSnapshot(domain.Domain, domain.SyncStatus))
                .ToArray() ?? [];
            var url = ResolvePublicServiceUrl(deployment.Url, serviceDomains);
            DateTimeOffset? lastDeployedAt = deployment.CreatedAt;

            return new ProviderServiceStatus(status, url, lastDeployedAt);
        }

        return new ProviderServiceStatus("unknown", null, null);
    }

    /// <summary>Redeploys a service, ensuring it has a public domain assigned as part of the redeploy (unlike Coolify's redeploy, which doesn't touch domains).</summary>
    public Task RedeployServiceAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);
        return RedeployServiceInstanceAsync(credentials, serviceId, environmentId, ensurePublicDomain: true, cancellationToken);
    }

    /// <summary>Deletes a service and any volumes attached to it (volumes aren't cleaned up automatically by Railway on service delete).</summary>
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

        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.DeleteService.ExecuteAsync(serviceId, cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
    }

    private async Task DeleteVolumeAsync(
        ProviderCredentials credentials,
        string volumeId,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.DeleteVolume.ExecuteAsync(volumeId, cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
    }

    public async Task RollbackDeploymentAsync(
        ProviderCredentials credentials,
        string providerDeploymentId,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.DeploymentRollback.ExecuteAsync(providerDeploymentId, cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
    }
}
