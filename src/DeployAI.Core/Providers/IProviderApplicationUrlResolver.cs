namespace DeployAI.Core.Providers;

/// <summary>
/// Optional capability for providers (Coolify) whose application URL must be looked up live from
/// the provider rather than being known statically from the deploy response.
/// </summary>
public interface IProviderApplicationUrlResolver
{
    string ProviderName { get; }

    /// <summary>Resolves an application's current live public URL.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side application id.</param>
    Task<string?> ResolveApplicationUrlAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);
}

/// <summary>Resolves the optional <see cref="IProviderApplicationUrlResolver"/> capability for a provider, if it supports one.</summary>
public interface IProviderApplicationUrlResolverFactory
{
    IProviderApplicationUrlResolver? GetResolver(string providerName);
}
