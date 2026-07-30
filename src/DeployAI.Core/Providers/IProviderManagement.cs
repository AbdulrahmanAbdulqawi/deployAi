namespace DeployAI.Core.Providers;

/// <summary>
/// Provider-side project/application management: creating a new app and managing its environment
/// variables. Implemented by every provider (unlike the optional capability interfaces), since
/// every deploy target needs to be created and have env vars set.
/// </summary>
public interface IProviderManagement
{
    string ProviderName { get; }

    /// <summary>Creates a new project/application on the provider for a GitHub repo.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="request">Repo, branch, framework, and build config for the new app.</param>
    Task<ProviderProject> CreateProjectAsync(
        ProviderCredentials credentials,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken);

    /// <summary>Lists environment variables set on a provider project.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side project/service id.</param>
    Task<IReadOnlyList<ProviderEnvVar>> ListEnvVarsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    /// <summary>Creates or updates an environment variable on a provider project.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side project/service id.</param>
    /// <param name="request">The key/value (and, where supported, type/targets) to set.</param>
    Task<ProviderEnvVar> UpsertEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        UpsertProviderEnvVarRequest request,
        CancellationToken cancellationToken);

    /// <summary>Deletes an environment variable from a provider project.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side project/service id.</param>
    /// <param name="envVarId">The provider's id for the env var to delete.</param>
    Task DeleteEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string envVarId,
        CancellationToken cancellationToken);

    /// <summary>Deletes a provider project/application entirely.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side project/service id to delete.</param>
    Task DeleteProjectAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes duplicate records for the same environment variable key, returning how many were
    /// deleted. Providers that cannot hold duplicates return zero.
    /// </summary>
    /// <remarks>
    /// Coolify stores env vars as rows and, before the write path became atomic, a concurrent
    /// list-then-create could leave several for one key. Its bulk handler resolves with
    /// <c>->where('key', $key)->first()</c>, so it writes to the first and leaves the rest stale —
    /// meaning what an app reads and what the UI shows can disagree indefinitely. One application
    /// was found with 32 records for 16 keys, including two <c>DATABASE_URL</c>s pointing at
    /// different Postgres instances, one of which no longer existed.
    /// <para>
    /// Default implementation is a no-op so a provider without the problem need not think about it.
    /// </para>
    /// </remarks>
    Task<int> ReconcileDuplicateEnvVarsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>Resolves the registered <see cref="IProviderManagement"/> implementation for a provider name.</summary>
public interface IProviderManagementFactory
{
    IProviderManagement GetManagement(string providerName);
}
