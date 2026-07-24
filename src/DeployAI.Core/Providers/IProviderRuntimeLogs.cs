namespace DeployAI.Core.Providers;

/// <summary>
/// Read the runtime stdout/stderr of a deployed application's containers — what
/// <c>docker logs</c> would show, not the build/deploy log. This is the only way to
/// diagnose a crash-looping container: build logs say "Built", the status API says
/// "exited", and neither says why.
///
/// A separate capability like <see cref="IProviderLifecycleOperations"/> because most
/// platforms don't expose it; resolve through <see cref="IProviderRuntimeLogsFactory"/>
/// and treat null as "not supported here".
/// </summary>
public interface IProviderRuntimeLogs
{
    string ProviderName { get; }

    /// <summary>Returns the last <paramref name="lines"/> lines of the application's container output.</summary>
    Task<string> GetRuntimeLogsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        int lines,
        CancellationToken cancellationToken);
}

public interface IProviderRuntimeLogsFactory
{
    IProviderRuntimeLogs? GetRuntimeLogs(string providerName);
}
