namespace DeployAI.Core.Providers;

/// <summary>
/// Start/stop/restart a deployed application without redeploying it.
///
/// A separate capability rather than part of <see cref="IProviderServiceOperations"/> because
/// not every provider exposes it — a platform that only knows how to redeploy shouldn't be
/// forced to throw from three more methods. Resolve it through
/// <see cref="IProviderLifecycleOperationsFactory"/> and treat null as "not supported here".
/// </summary>
public interface IProviderLifecycleOperations
{
    string ProviderName { get; }

    Task StartApplicationAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    Task StopApplicationAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);

    Task RestartApplicationAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);
}

public interface IProviderLifecycleOperationsFactory
{
    IProviderLifecycleOperations? GetLifecycleOperations(string providerName);
}
