namespace DeployAI.Core.Domains;

/// <summary>
/// Reads the certificate a hostname actually serves, including the invalid ones — an ordinary HTTP
/// request throws on those, which is how a self-signed fallback certificate stays invisible behind
/// a deploy that reported success.
/// </summary>
public interface ICertificateInspector
{
    /// <summary>
    /// Opens a TLS connection to <paramref name="hostname"/> on port 443 and reports what it is
    /// served. Never throws for a certificate problem; an unreachable host is
    /// <see cref="CertificateOutcome.Unreachable"/>, not an exception.
    /// </summary>
    Task<CertificateInspection> InspectAsync(string hostname, CancellationToken cancellationToken);
}
