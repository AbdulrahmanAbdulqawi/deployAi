namespace DeployAI.Core.Providers;

/// <summary>
/// Attaches a Docker Compose deployment's domain to the one service that faces the browser.
/// Coolify (the only implementer today) cannot accept a compose app's domain at creation time —
/// it only exists once the first deploy has parsed the compose file — so this is a post-deploy
/// step, and its effect on routing needs one further deploy to reach the proxy.
/// </summary>
public interface IComposeDomainAssignment
{
    /// <summary>
    /// Assigns a domain to <paramref name="serviceName"/> within the compose application
    /// identified by <paramref name="providerProjectId"/>. Idempotent — safe to call on every
    /// deploy, including when the domain is already assigned. Pass <paramref name="domain"/> as
    /// null to let the implementer derive its own default rather than requiring the caller to
    /// already know a domain that may not exist anywhere yet.
    /// </summary>
    /// <returns>The domain actually assigned, or null if none could be determined and nothing was attempted.</returns>
    Task<string?> AssignComposeDomainAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string? domain,
        string? serviceName,
        CancellationToken cancellationToken);
}
