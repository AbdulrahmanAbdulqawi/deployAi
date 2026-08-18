using DeployAI.Core.Domains;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;

namespace DeployAI.Infrastructure.Dns;

/// <summary>
/// Queries public resolvers directly rather than through the machine's own stub resolver.
/// </summary>
/// <remarks>
/// <para>
/// <c>System.Net.Dns</c> cannot answer the questions this needs. It uses whatever resolver the
/// container was handed, so it can disagree with the one Coolify validates against; it returns
/// addresses rather than records, so a CNAME — which Coolify will not accept — looks like a
/// perfectly good answer; it reports a timeout and a non-existent name as the same
/// <c>SocketException</c>, collapsing "could not look" into "found nothing"; and it has no concept
/// of CAA, which is the one record that makes certificate issuance fail every single time.
/// </para>
/// <para>
/// Caching is off deliberately. This runs in a propagation loop, and a cached negative answer is
/// the exact value that must not be remembered.
/// </para>
/// </remarks>
public sealed class DnsClientResolver : IDnsResolver
{
    // 1.1.1.1 first because it is the resolver Coolify itself validates domains against: checking a
    // different one risks green-lighting an assignment Coolify then refuses.
    private static readonly string[] ResolverAddresses = ["1.1.1.1", "8.8.8.8", "9.9.9.9"];

    private readonly ILogger<DnsClientResolver> _logger;

    public DnsClientResolver(ILogger<DnsClientResolver> logger) => _logger = logger;

    public async Task<DnsCheckResult> CheckAsync(
        string hostname,
        string expectedAddress,
        CancellationToken cancellationToken)
    {
        var observations = new List<DnsObservation>(ResolverAddresses.Length);

        foreach (var resolverAddress in ResolverAddresses)
        {
            observations.Add(await ObserveAsync(resolverAddress, hostname, cancellationToken));
        }

        return DnsObservationCombiner.Combine(hostname, expectedAddress, observations);
    }

    private async Task<DnsObservation> ObserveAsync(
        string resolverAddress,
        string hostname,
        CancellationToken cancellationToken)
    {
        var client = new LookupClient(new LookupClientOptions(System.Net.IPAddress.Parse(resolverAddress))
        {
            UseCache = false,
            // A response code is data, not an exception: NXDOMAIN has to stay distinguishable from
            // a server failure, and throwing on both would merge them again.
            ThrowDnsErrors = false,
            Timeout = TimeSpan.FromSeconds(5),
            Retries = 1
        });

        try
        {
            var address = await client.QueryAsync(hostname, QueryType.A, cancellationToken: cancellationToken);

            if (address.HasError && !string.Equals(address.Header.ResponseCode.ToString(), "NotExistentDomain", StringComparison.OrdinalIgnoreCase))
            {
                // A server failure means this resolver could not tell us, which is not the same as
                // telling us the name does not exist.
                return DnsObservation.Unreachable(resolverAddress, address.ErrorMessage);
            }

            var addresses = address.Answers
                .OfType<ARecord>()
                .Select(record => record.Address.ToString())
                .ToList();

            var alias = address.Answers.OfType<CNameRecord>().FirstOrDefault()?.CanonicalName.Value.TrimEnd('.');

            return new DnsObservation(
                resolverAddress,
                Answered: true,
                addresses,
                addresses.Count == 0 ? alias : null,
                await ReadCertificateAuthoritiesAsync(client, hostname, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Resolver {Resolver} could not be reached for {Hostname}.", resolverAddress, hostname);
            return DnsObservation.Unreachable(resolverAddress, ex.Message);
        }
    }

    /// <summary>
    /// Reads the domain's CAA records. An empty list means no CAA was published, which permits every
    /// authority; a failed lookup returns empty too, and that is acceptable here only because CAA is
    /// used to refuse early, never to approve.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadCertificateAuthoritiesAsync(
        ILookupClient client,
        string hostname,
        CancellationToken cancellationToken)
    {
        try
        {
            var caa = await client.QueryAsync(hostname, QueryType.CAA, cancellationToken: cancellationToken);
            return caa.Answers
                .OfType<CaaRecord>()
                .Where(record => string.Equals(record.Tag, "issue", StringComparison.OrdinalIgnoreCase))
                .Select(record => record.Value)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
