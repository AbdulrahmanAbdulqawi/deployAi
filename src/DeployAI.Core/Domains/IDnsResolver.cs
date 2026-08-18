namespace DeployAI.Core.Domains;

/// <summary>
/// Asks public resolvers where a hostname points, so a domain can be proven to reach the server
/// before anything asks a certificate authority to vouch for it.
/// </summary>
public interface IDnsResolver
{
    /// <summary>
    /// Checks <paramref name="hostname"/> against <paramref name="expectedAddress"/>. Never throws
    /// for a DNS-level answer: a name that does not exist, a name behind a CDN, and a resolver that
    /// could not be reached are all outcomes on the returned result rather than exceptions.
    /// </summary>
    /// <param name="hostname">An ASCII (punycode) hostname, without scheme or trailing dot.</param>
    /// <param name="expectedAddress">The server's IPv4 address the record should resolve to.</param>
    Task<DnsCheckResult> CheckAsync(
        string hostname,
        string expectedAddress,
        CancellationToken cancellationToken);
}
