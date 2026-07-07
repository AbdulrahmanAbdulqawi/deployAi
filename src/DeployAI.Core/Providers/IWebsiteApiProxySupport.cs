namespace DeployAI.Core.Providers;

/// <summary>
/// Vercel-specific project routing used to proxy /api and /hubs to an external API origin.
/// </summary>
public interface IWebsiteApiProxySupport
{
    Task EnsureApiProxyRoutesAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string apiBaseUrl,
        CancellationToken cancellationToken);

    Task<string?> ResolvePublicWebsiteUrlAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string? deploymentUrl,
        CancellationToken cancellationToken);

    Task AssignProductionDomainsToDeploymentAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string deploymentId,
        CancellationToken cancellationToken);

    Task<string?> GetLatestProductionDeploymentIdAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListProductionWebsiteOriginsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);
}
