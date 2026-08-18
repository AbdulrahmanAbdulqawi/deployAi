using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DeployAI.Core.Domains;
using Microsoft.Extensions.Logging;

namespace DeployAI.Infrastructure.Dns;

/// <summary>
/// Reads the certificate a host serves by doing the TLS handshake directly and accepting whatever
/// comes back.
/// </summary>
/// <remarks>
/// <para>
/// An <c>HttpClient</c> cannot do this job: its default validation throws on exactly the
/// certificate worth observing. When an ACME challenge fails, Traefik installs its own self-signed
/// certificate and keeps serving — the site is up, the route works, and every browser refuses it.
/// A probe that treats that handshake failure as an unreachable host reports the wrong problem and
/// suggests the wrong fix.
/// </para>
/// <para>
/// The target host is set explicitly on the handshake because Traefik picks its router by SNI.
/// Connecting without it returns the default certificate no matter which domain was asked for,
/// which would make every check look like a failed issuance.
/// </para>
/// </remarks>
public sealed class SslStreamCertificateInspector : ICertificateInspector
{
    // Traefik's built-in fallback leaf. Recognising it by name is what separates "the certificate
    // has not been issued yet" from "something else is serving TLS here".
    private const string TraefikDefaultSubject = "TRAEFIK DEFAULT CERT";

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<SslStreamCertificateInspector> _logger;

    public SslStreamCertificateInspector(ILogger<SslStreamCertificateInspector> logger) => _logger = logger;

    public async Task<CertificateInspection> InspectAsync(string hostname, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(hostname, 443, timeout.Token);

            await using var ssl = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                // Accept everything: an invalid certificate is the observation, not an error.
                userCertificateValidationCallback: (_, _, _, _) => true);

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = hostname },
                timeout.Token);

            return ssl.RemoteCertificate is null
                ? Unreachable(hostname, "The server completed a TLS handshake without presenting a certificate.")
                : Describe(hostname, new X509Certificate2(ssl.RemoteCertificate));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || timeout.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not inspect the certificate served for {Hostname}.", hostname);
            return Unreachable(hostname, $"Could not open a TLS connection to {hostname}: {ex.Message}");
        }
    }

    internal static CertificateInspection Describe(string hostname, X509Certificate2 certificate)
    {
        var names = ReadSubjectAlternativeNames(certificate).ToList();
        var issuer = certificate.Issuer;
        var subject = certificate.Subject;
        var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());

        var isTraefikDefault =
            subject.Contains(TraefikDefaultSubject, StringComparison.OrdinalIgnoreCase) ||
            issuer.Contains(TraefikDefaultSubject, StringComparison.OrdinalIgnoreCase);

        var outcome =
            isTraefikDefault ? CertificateOutcome.ProxyDefault
            : string.Equals(issuer, subject, StringComparison.OrdinalIgnoreCase) ? CertificateOutcome.SelfSigned
            : !CoversHostname(hostname, names, subject) ? CertificateOutcome.HostnameMismatch
            : notAfter <= DateTimeOffset.UtcNow ? CertificateOutcome.Expired
            : CertificateOutcome.Valid;

        return new CertificateInspection(
            hostname,
            outcome,
            issuer,
            subject,
            notBefore,
            notAfter,
            names,
            [DescribeOutcome(hostname, outcome, issuer, notAfter)]);
    }

    private static string DescribeOutcome(
        string hostname,
        CertificateOutcome outcome,
        string issuer,
        DateTimeOffset notAfter) => outcome switch
        {
            CertificateOutcome.ProxyDefault =>
                $"{hostname} is served the proxy's own fallback certificate, which means the " +
                "certificate request ran and failed. The site is reachable but every browser will " +
                "warn on it.",
            CertificateOutcome.SelfSigned =>
                $"{hostname} is served a self-signed certificate ({issuer}).",
            CertificateOutcome.HostnameMismatch =>
                $"The certificate served for {hostname} does not cover that name.",
            CertificateOutcome.Expired =>
                $"The certificate for {hostname} expired on {notAfter:u}.",
            _ => $"{hostname} is served a valid certificate from {issuer}, good until {notAfter:u}."
        };

    private static CertificateInspection Unreachable(string hostname, string finding) =>
        new(hostname, CertificateOutcome.Unreachable, null, null, null, null, [], [finding]);

    private static bool CoversHostname(string hostname, IReadOnlyList<string> names, string subject)
    {
        if (names.Any(name => MatchesHostname(hostname, name)))
        {
            return true;
        }

        // Fall back to the common name for certificates old enough not to carry a SAN extension.
        var commonName = subject
            .Split(',', StringSplitOptions.TrimEntries)
            .FirstOrDefault(part => part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))?[3..];

        return commonName is not null && MatchesHostname(hostname, commonName);
    }

    internal static bool MatchesHostname(string hostname, string candidate)
    {
        if (string.Equals(hostname, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!candidate.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        // A wildcard covers exactly one label, so app.example.com matches *.example.com but
        // a.b.example.com does not.
        var suffix = candidate[1..];
        if (!hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var label = hostname[..^suffix.Length];
        return label.Length > 0 && !label.Contains('.');
    }

    /// <summary>
    /// The DNS names a certificate covers, read from the SAN extension's ASN.1 rather than from its
    /// rendered text.
    /// </summary>
    /// <remarks>
    /// <c>X509Extension.Format</c> is platform-dependent, and the difference is silent. On Windows it
    /// renders <c>DNS Name=app.example.com</c> lines, which parse; on Linux it renders something else
    /// entirely, so splitting on '=' returned an empty list and every certificate looked as though it
    /// covered no names at all. Since <see cref="CoversHostname"/> decides
    /// <see cref="CertificateOutcome.HostnameMismatch"/>, that meant a perfectly good certificate —
    /// one covering the host through SAN rather than a legacy common name, which is how every modern
    /// certificate is issued — was reported as a mismatch anywhere this ran on Linux. Which is
    /// everywhere it runs in production.
    /// <para>
    /// <see cref="X509SubjectAlternativeNameExtension"/> parses the extension itself and gives the
    /// same answer on every platform.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> ReadSubjectAlternativeNames(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17")
            {
                continue;
            }

            var subjectAlternativeNames =
                new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);

            foreach (var name in subjectAlternativeNames.EnumerateDnsNames())
            {
                yield return name;
            }
        }
    }
}
