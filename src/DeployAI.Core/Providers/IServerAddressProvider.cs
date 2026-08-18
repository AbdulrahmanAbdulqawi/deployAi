namespace DeployAI.Core.Providers;

/// <summary>
/// Answers the one question a domain cannot be verified without: which IP address should this
/// project's DNS record point at.
/// </summary>
/// <remarks>
/// A capability rather than part of <c>IDeploymentProvider</c>, because it only means something for
/// a provider that runs apps on a server the user's DNS can name. A platform that fronts apps with
/// its own managed hostnames has no answer to give and should not implement this.
/// </remarks>
public interface IServerAddressProvider
{
    string ProviderName { get; }

    /// <summary>
    /// The IPv4 address apps on this connection are reachable at, or null when it cannot be
    /// determined — which is a reason to stop, not to guess. A wrong address here sends the user to
    /// change DNS to somewhere their app is not.
    /// </summary>
    /// <param name="serverUuid">
    /// Which server, when the connection has more than one. Null picks the only server if there is
    /// exactly one, and returns null rather than choosing arbitrarily if there are several.
    /// </param>
    Task<string?> TryGetServerAddressAsync(
        ProviderCredentials credentials,
        string? serverUuid,
        CancellationToken cancellationToken);
}

public interface IServerAddressProviderFactory
{
    IServerAddressProvider? GetServerAddressProvider(string providerName);
}
