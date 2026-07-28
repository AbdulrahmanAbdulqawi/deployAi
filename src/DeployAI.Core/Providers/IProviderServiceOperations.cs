namespace DeployAI.Core.Providers;

/// <summary>A provider service's live status - running/deploying/failed, its current URL, and when it last deployed.</summary>
public sealed record ProviderServiceStatus(
    string Status,
    string? DeployUrl,
    DateTimeOffset? LastDeployedAt);

/// <summary>
/// Ad-hoc operations on an already-created provider service, outside the normal deploy pipeline:
/// checking live status, restarting, deleting, and rolling back.
/// </summary>
public interface IProviderServiceOperations
{
    string ProviderName { get; }

    /// <summary>Gets a service's current live status.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side project/service id.</param>
    Task<ProviderServiceStatus> GetServiceStatusAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    /// <summary>Restarts/redeploys a service without pushing new code (picks up current env vars/config).</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side project/service id.</param>
    Task RedeployServiceAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    /// <summary>Deletes a service from the provider.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side project/service id to delete.</param>
    Task DeleteServiceAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    /// <summary>Rolls a service back to a previous deployment. Not every provider supports this (e.g. Coolify doesn't).</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerDeploymentId">The provider's id for the deployment to roll back to.</param>
    Task RollbackDeploymentAsync(
        ProviderCredentials credentials,
        string providerDeploymentId,
        CancellationToken cancellationToken);
}

/// <summary>Resolves the optional <see cref="IProviderServiceOperations"/> capability for a provider, if it supports one.</summary>
public interface IProviderServiceOperationsFactory
{
    IProviderServiceOperations? GetServiceOperations(string providerName);
}
