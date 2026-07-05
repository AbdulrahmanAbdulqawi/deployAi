namespace DeployAI.Core.Providers;

public interface IDeploymentProvider
{
    string ProviderName { get; }
    string DisplayName { get; }
    string ApiStyle { get; }

    Task<DeploymentResponse> TriggerDeploymentAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string branch,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);

    Task<DeploymentStatus> GetStatusAsync(
        ProviderCredentials credentials,
        string deploymentId,
        CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamLogsAsync(
        ProviderCredentials credentials,
        string deploymentId,
        CancellationToken cancellationToken);

    Task<bool> ValidateCredentialsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderProject>> ListProjectsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken);
}
