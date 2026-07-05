namespace DeployAI.Core.Providers;

public interface IProviderFactory
{
    IDeploymentProvider GetProvider(string providerName);
    IReadOnlyList<ProviderInfo> GetAvailableProviders();
}

public sealed record ProviderInfo(string Name, string DisplayName, string ApiStyle);
