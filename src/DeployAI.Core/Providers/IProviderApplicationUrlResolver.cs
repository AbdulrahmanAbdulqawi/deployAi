namespace DeployAI.Core.Providers;

public interface IProviderApplicationUrlResolver
{
    string ProviderName { get; }

    Task<string?> ResolveApplicationUrlAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);
}

public interface IProviderApplicationUrlResolverFactory
{
    IProviderApplicationUrlResolver? GetResolver(string providerName);
}
