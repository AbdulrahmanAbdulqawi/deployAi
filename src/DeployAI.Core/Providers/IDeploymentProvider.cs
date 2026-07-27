namespace DeployAI.Core.Providers;

/// <summary>
/// Core deployment lifecycle operations every hosting provider (Vercel, Railway, Coolify) must
/// implement: triggering a deploy, polling its status, streaming its logs, and validating a
/// connection. This is the contract <see cref="IProviderFactory"/> resolves by provider name.
/// </summary>
public interface IDeploymentProvider
{
    /// <summary>The provider's internal identifier (e.g. "vercel", "railway", "coolify").</summary>
    string ProviderName { get; }

    /// <summary>The provider's human-readable name for display in the UI.</summary>
    string DisplayName { get; }

    /// <summary>The style of API the provider exposes (e.g. "rest", "graphql"), for diagnostics.</summary>
    string ApiStyle { get; }

    /// <summary>Starts a deployment of <paramref name="providerProjectId"/> at the given branch.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side project/service/application id.</param>
    /// <param name="branch">The git branch to deploy.</param>
    /// <param name="environment">Extra environment entries to apply for this deploy.</param>
    Task<DeploymentResponse> TriggerDeploymentAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string branch,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);

    /// <summary>Polls the current status of a previously triggered deployment.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="deploymentId">The provider's id for the deployment (from <see cref="TriggerDeploymentAsync"/>).</param>
    Task<DeploymentStatus> GetStatusAsync(
        ProviderCredentials credentials,
        string deploymentId,
        CancellationToken cancellationToken);

    /// <summary>Streams build/deploy log lines for a deployment as they become available.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="deploymentId">The provider's id for the deployment.</param>
    IAsyncEnumerable<string> StreamLogsAsync(
        ProviderCredentials credentials,
        string deploymentId,
        CancellationToken cancellationToken);

    /// <summary>Checks whether a connection's stored credentials are still valid.</summary>
    /// <param name="credentials">The connection to validate.</param>
    Task<bool> ValidateCredentialsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken);

    /// <summary>Lists the provider-side projects/applications visible to a connection.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    Task<IReadOnlyList<ProviderProject>> ListProjectsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken);
}
