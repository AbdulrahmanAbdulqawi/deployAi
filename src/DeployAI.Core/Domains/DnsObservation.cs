namespace DeployAI.Core.Domains;

/// <summary>What one resolver said when asked about a hostname.</summary>
/// <param name="Answered">
/// Whether this resolver produced a usable reply. An authoritative "no such name" counts as
/// answered — it is information. A timeout or a server failure does not.
/// </param>
/// <param name="Note">Why it could not answer, when it could not. Surfaced to the user verbatim.</param>
public sealed record DnsObservation(
    string Resolver,
    bool Answered,
    IReadOnlyList<string> Addresses,
    string? AliasTarget = null,
    IReadOnlyList<string>? CertificateAuthorities = null,
    string? Note = null)
{
    public static DnsObservation Unreachable(string resolver, string note) =>
        new(resolver, Answered: false, Addresses: [], Note: note);
}

public static class DnsObservationCombiner
{
    /// <summary>
    /// Folds every resolver's answer into one result, and writes the findings that explain it.
    /// </summary>
    /// <remarks>
    /// Findings are built here rather than by the caller so that no path can produce a result with
    /// an empty explanation. A check that reports nothing reads exactly like a check that never
    /// ran, and the two lead to opposite decisions.
    /// </remarks>
    public static DnsCheckResult Combine(
        string hostname,
        string expectedAddress,
        IReadOnlyList<DnsObservation> observations)
    {
        var queried = observations.Select(o => o.Resolver).ToList();
        var answered = observations.Where(o => o.Answered).Select(o => o.Resolver).ToList();

        var addresses = observations
            .Where(o => o.Answered)
            .SelectMany(o => o.Addresses)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var alias = observations.FirstOrDefault(o => o.Answered && o.AliasTarget is not null)?.AliasTarget;

        var authorities = observations
            .Where(o => o.Answered)
            .SelectMany(o => o.CertificateAuthorities ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new DnsCheckResult(
            hostname,
            expectedAddress,
            queried,
            answered,
            addresses,
            alias,
            authorities,
            Findings: []);

        return result with { Findings = Describe(result, observations) };
    }

    private static List<string> Describe(
        DnsCheckResult result,
        IReadOnlyList<DnsObservation> observations)
    {
        var findings = new List<string>();

        foreach (var unreachable in observations.Where(o => !o.Answered))
        {
            findings.Add($"{unreachable.Resolver} did not answer: {unreachable.Note ?? "no reply"}.");
        }

        if (result.IsInconclusive)
        {
            findings.Add(
                $"Could not check where {result.Hostname} points — no resolver answered. This says " +
                "nothing about the domain's records.");
            return findings;
        }

        if (result.PointsAtTarget)
        {
            findings.Add($"{result.Hostname} resolves to {result.ExpectedAddress}.");
        }
        else if (result.IsProxiedByCdn)
        {
            findings.Add(
                $"{result.Hostname} resolves to a CDN edge ({string.Join(", ", result.ObservedAddresses)}) " +
                $"rather than to {result.ExpectedAddress}. Proxied records break the HTTP-01 challenge, " +
                "so the record has to be DNS-only until the certificate is issued.");
        }
        else if (result.AliasTarget is not null)
        {
            findings.Add(
                $"{result.Hostname} is a CNAME to {result.AliasTarget}. An A record pointing at " +
                $"{result.ExpectedAddress} is required instead.");
        }
        else if (result.ObservedAddresses.Count == 0)
        {
            findings.Add($"{result.Hostname} has no address records yet.");
        }
        else
        {
            findings.Add(
                $"{result.Hostname} resolves to {string.Join(", ", result.ObservedAddresses)}, " +
                $"not to {result.ExpectedAddress}.");
        }

        if (result.BlocksLetsEncrypt)
        {
            findings.Add(
                $"The domain's CAA records allow only {string.Join(", ", result.CertificateAuthorities)}, " +
                "so Let's Encrypt cannot issue for it. Every certificate attempt will fail until a " +
                "CAA record for letsencrypt.org is added.");
        }

        return findings;
    }
}
