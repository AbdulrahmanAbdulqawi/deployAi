namespace DeployAI.Core.Providers;

public interface IProviderManagement
{
    string ProviderName { get; }

    Task<ProviderProject> CreateProjectAsync(
        ProviderCredentials credentials,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderEnvVar>> ListEnvVarsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    Task<ProviderEnvVar> UpsertEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        UpsertProviderEnvVarRequest request,
        CancellationToken cancellationToken);

    Task DeleteEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string envVarId,
        CancellationToken cancellationToken);
}

public interface IProviderManagementFactory
{
    IProviderManagement GetManagement(string providerName);
}
