namespace DeployAI.Core.Providers;

/// <summary>
/// Vercel-specific project routing used to proxy /api and /hubs to an external API origin.
/// </summary>
public interface IWebsiteApiProxySupport
{
    /// <summary>Ensures vercel.json's /api and /hubs rewrites point at the given API origin (same-origin proxy mode).</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The Vercel project id.</param>
    /// <param name="apiBaseUrl">The backend origin to proxy requests to.</param>
    Task EnsureApiProxyRoutesAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string apiBaseUrl,
        CancellationToken cancellationToken);

    /// <summary>Resolves the project's stable public URL (its assigned domain), falling back to <paramref name="deploymentUrl"/> if none is assigned.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The Vercel project id.</param>
    /// <param name="deploymentUrl">A specific deployment's URL to fall back to.</param>
    Task<string?> ResolvePublicWebsiteUrlAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string? deploymentUrl,
        CancellationToken cancellationToken);

    /// <summary>Promotes a specific deployment to production by assigning the project's domains to it.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The Vercel project id.</param>
    /// <param name="deploymentId">The deployment to promote.</param>
    Task AssignProductionDomainsToDeploymentAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string deploymentId,
        CancellationToken cancellationToken);

    /// <summary>Gets the id of the project's current production deployment, if any.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The Vercel project id.</param>
    Task<string?> GetLatestProductionDeploymentIdAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    /// <summary>Lists every production domain/origin currently assigned to the project.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The Vercel project id.</param>
    Task<IReadOnlyList<string>> ListProductionWebsiteOriginsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);
}
