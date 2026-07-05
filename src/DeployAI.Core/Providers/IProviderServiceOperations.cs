namespace DeployAI.Core.Providers;

public sealed record ProviderServiceStatus(
    string Status,
    string? DeployUrl,
    DateTimeOffset? LastDeployedAt);

public interface IProviderServiceOperations
{
    string ProviderName { get; }

    Task<ProviderServiceStatus> GetServiceStatusAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    Task RedeployServiceAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    Task DeleteServiceAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);
}

public interface IProviderServiceOperationsFactory
{
    IProviderServiceOperations? GetServiceOperations(string providerName);
}
