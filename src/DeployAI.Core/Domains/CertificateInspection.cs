namespace DeployAI.Core.Domains;

/// <summary>What a TLS handshake against a deployed hostname revealed about its certificate.</summary>
public enum CertificateOutcome
{
    /// <summary>The handshake never completed, so nothing was learned about the certificate.</summary>
    Unreachable = 0,

    /// <summary>
    /// The proxy is serving its own fallback certificate. This is the recognisable signature of an
    /// ACME challenge that ran and failed, and the single most useful diagnostic in the flow — the
    /// site is up, the route exists, and the certificate is the thing that did not happen.
    /// </summary>
    ProxyDefault = 1,

    /// <summary>Self-signed or otherwise not chained to a public authority, but not the proxy's default.</summary>
    SelfSigned = 2,

    /// <summary>A real certificate, but none of its names cover the hostname asked for.</summary>
    HostnameMismatch = 3,

    /// <summary>A real certificate for the right host, past its expiry.</summary>
    Expired = 4,

    /// <summary>A publicly-issued, in-date certificate covering the hostname.</summary>
    Valid = 5
}

/// <summary>
/// One look at the certificate a hostname actually serves.
/// </summary>
/// <remarks>
/// Deliberately reports an invalid certificate rather than refusing it. The ordinary HTTP client
/// path throws on a self-signed certificate, which is exactly the case worth observing: a deploy
/// that succeeded, a route that exists, and a browser warning nobody saw because the probe treated
/// the handshake failure as an unreachable host.
/// </remarks>
/// <param name="Findings">
/// Always populated — a verification that reports nothing is indistinguishable from one that never
/// ran.
/// </param>
public sealed record CertificateInspection(
    string Hostname,
    CertificateOutcome Outcome,
    string? Issuer,
    string? Subject,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    IReadOnlyList<string> SubjectAlternativeNames,
    IReadOnlyList<string> Findings)
{
    /// <summary>True when the handshake never completed — "could not check", not "the certificate is bad".</summary>
    public bool IsInconclusive => Outcome is CertificateOutcome.Unreachable;

    /// <summary>True when a browser would accept this certificate for this hostname.</summary>
    public bool IsIssued => Outcome is CertificateOutcome.Valid;
}
