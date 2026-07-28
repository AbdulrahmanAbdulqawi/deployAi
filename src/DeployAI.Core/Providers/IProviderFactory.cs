namespace DeployAI.Core.Providers;

/// <summary>Resolves the registered <see cref="IDeploymentProvider"/> for a given provider name.</summary>
public interface IProviderFactory
{
    /// <summary>Gets the provider implementation by name. Throws if the name isn't registered.</summary>
    IDeploymentProvider GetProvider(string providerName);

    /// <summary>Lists every hosting provider registered with the app.</summary>
    IReadOnlyList<ProviderInfo> GetAvailableProviders();
}

/// <summary>Display metadata for a registered hosting provider.</summary>
public sealed record ProviderInfo(string Name, string DisplayName, string ApiStyle);
