namespace DeployAI.Core.Domains;

/// <summary>What a DNS check concluded about where a hostname points.</summary>
public enum DnsOutcome
{
    /// <summary>No resolver produced a usable answer — nothing was learned.</summary>
    Inconclusive = 0,

    /// <summary>A resolver answered authoritatively that the name has no address records.</summary>
    NotFound = 1,

    /// <summary>The name resolves, but not to the server this app is deployed on.</summary>
    Mismatch = 2,

    /// <summary>The name resolves to the expected server address.</summary>
    Matches = 3
}

/// <summary>
/// One DNS check of a hostname against the address it is supposed to point at, across every
/// resolver that was asked.
/// </summary>
/// <remarks>
/// The separation between <see cref="IsInconclusive"/> and <see cref="DnsOutcome.NotFound"/> is the
/// point of this type. A resolver that answers "this name has no address" has told us something;
/// a resolver that times out has not. Collapsing the two turns a network blip into "your DNS is
/// wrong", which is both false and the kind of message a non-technical user cannot argue with.
/// </remarks>
/// <param name="ResolversQueried">Every resolver asked, whether or not it replied. Always populated.</param>
/// <param name="ResolversAnswered">Those that replied, an authoritative "no records" included.</param>
/// <param name="ObservedAddresses">The A/AAAA addresses seen, across all answering resolvers.</param>
/// <param name="AliasTarget">
/// Set when the name is a CNAME. Coolify needs an A record pointing at the server; a CNAME resolves
/// perfectly well and still will not do, so the shape has to be reported separately from the
/// address it happens to reach.
/// </param>
/// <param name="CertificateAuthorities">
/// The issuers named by the domain's CAA records, if any. An empty list means no CAA was published
/// (every authority is allowed) — it does not mean the lookup failed; that is
/// <see cref="IsInconclusive"/>.
/// </param>
/// <param name="Findings">
/// Human-readable notes. Always populated — a check that reports nothing is indistinguishable from
/// one that never ran.
/// </param>
public sealed record DnsCheckResult(
    string Hostname,
    string ExpectedAddress,
    IReadOnlyList<string> ResolversQueried,
    IReadOnlyList<string> ResolversAnswered,
    IReadOnlyList<string> ObservedAddresses,
    string? AliasTarget,
    IReadOnlyList<string> CertificateAuthorities,
    IReadOnlyList<string> Findings)
{
    /// <summary>True when no resolver answered — "could not look", never "found nothing".</summary>
    public bool IsInconclusive => ResolversAnswered.Count == 0;

    /// <summary>True when the name resolves to the server this app is deployed on.</summary>
    public bool PointsAtTarget =>
        !IsInconclusive &&
        ObservedAddresses.Contains(ExpectedAddress, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name resolves only to a CDN's own edge addresses. Worth separating from a plain mismatch
    /// because the remediation is different and unguessable: the record is not wrong, it is
    /// proxied, and an HTTP-01 challenge will keep failing until it is set to DNS-only.
    /// </summary>
    public bool IsProxiedByCdn =>
        !IsInconclusive &&
        !PointsAtTarget &&
        ObservedAddresses.Count > 0 &&
        ObservedAddresses.All(CdnAddressRanges.IsKnownProxyAddress);

    /// <summary>
    /// A CAA record exists and does not permit Let's Encrypt. Every ACME attempt will fail, so this
    /// is worth refusing on rather than discovering five failed validations later.
    /// </summary>
    public bool BlocksLetsEncrypt =>
        CertificateAuthorities.Count > 0 &&
        !CertificateAuthorities.Any(issuer =>
            issuer.Contains("letsencrypt.org", StringComparison.OrdinalIgnoreCase));

    public DnsOutcome Outcome =>
        IsInconclusive ? DnsOutcome.Inconclusive
        : PointsAtTarget ? DnsOutcome.Matches
        : ObservedAddresses.Count == 0 && AliasTarget is null ? DnsOutcome.NotFound
        : DnsOutcome.Mismatch;
}
