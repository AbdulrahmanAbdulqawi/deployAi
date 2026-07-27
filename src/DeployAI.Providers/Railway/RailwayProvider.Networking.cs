using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;

namespace DeployAI.Providers.Railway;

/// <summary>Ensures a service has a public Railway domain assigned (skipped for database-role services), and resolves a service's current public URL.</summary>
public sealed partial class RailwayProvider
{
    /// <summary>Creates a public domain for a service if it doesn't already have one, unless the service is a database or explicitly opted out.</summary>
    private async Task EnsurePublicServiceDomainAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        if (ShouldSkipPublicNetworking(environment))
        {
            return;
        }

        await using var gql = _graphQl.CreateSession(credentials);
        var existingDomains = await gql.Client.ServiceInstanceDomains.ExecuteAsync(
            serviceId,
            environmentId,
            cancellationToken);
        var domainData = RailwayApiSupport.TryGetData(existingDomains, static _ => false);
        var serviceDomains = domainData?.ServiceInstance?.Domains?.ServiceDomains?
            .Select(domain => new ServiceDomainSnapshot(domain.Domain, domain.SyncStatus))
            .ToArray() ?? [];
        if (HasActiveServiceDomain(serviceDomains))
        {
            return;
        }

        var createResult = await gql.Client.ServiceDomainCreate.ExecuteAsync(
            new ServiceDomainCreateInput
            {
                ServiceId = serviceId,
                EnvironmentId = environmentId,
                TargetPort = ResolveTargetPort(environment)
            },
            cancellationToken);
        RailwayApiSupport.EnsureSuccess(createResult);
    }

    private static bool ShouldSkipPublicNetworking(IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return false;
        }

        if (environment.TryGetValue("role", out var role) &&
            string.Equals(role, "database", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return environment.TryGetValue("skipPublicNetworking", out var skip) &&
               string.Equals(skip, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasActiveServiceDomain(IEnumerable<ServiceDomainSnapshot> serviceDomains) =>
        ResolvePublicServiceUrl(deploymentUrl: null, serviceDomains) is not null;

    private static int? ResolveTargetPort(IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return null;
        }

        foreach (var key in new[] { "targetPort", "port", "listenPort" })
        {
            if (environment.TryGetValue(key, out var value) &&
                int.TryParse(value, out var port) &&
                port > 0)
            {
                return port;
            }
        }

        if (environment.TryGetValue("framework", out var framework))
        {
            return framework.ToLowerInvariant() switch
            {
                "dotnet" or "aspnet" or "aspnetcore" or "docker" => 8080,
                "node" or "express" or "nestjs" => 8080,
                _ => null
            };
        }

        return null;
    }

    /// <summary>Resolves a service's public URL: the deployment's own URL if known, else its first active (non-deleting) domain.</summary>
    internal static string? ResolvePublicServiceUrl(
        string? deploymentUrl,
        IEnumerable<ServiceDomainSnapshot> serviceDomains)
    {
        if (!string.IsNullOrWhiteSpace(deploymentUrl))
        {
            return deploymentUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   deploymentUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? deploymentUrl
                : $"https://{deploymentUrl}";
        }

        foreach (var domain in serviceDomains)
        {
            if (string.IsNullOrWhiteSpace(domain.Domain))
            {
                continue;
            }

            if (domain.SyncStatus is ServiceDomainSyncStatus.Deleted or ServiceDomainSyncStatus.Deleting)
            {
                continue;
            }

            return $"https://{domain.Domain}";
        }

        return null;
    }

    internal readonly record struct ServiceDomainSnapshot(string? Domain, ServiceDomainSyncStatus? SyncStatus);
}
