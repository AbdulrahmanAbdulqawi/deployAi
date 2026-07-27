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
}

/// <summary>Resolves the registered <see cref="IProviderManagement"/> implementation for a provider name.</summary>
public interface IProviderManagementFactory
{
    IProviderManagement GetManagement(string providerName);
}
