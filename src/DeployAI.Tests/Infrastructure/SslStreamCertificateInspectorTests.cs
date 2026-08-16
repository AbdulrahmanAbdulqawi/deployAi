using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeployAI.Core.Domains;
using DeployAI.Infrastructure.Dns;

namespace DeployAI.Tests.Infrastructure;

/// <summary>
/// Classification of a certificate actually served, against certificates built here rather than
/// mocked. The case worth the most is the proxy's fallback certificate: when an ACME challenge
/// fails, Traefik keeps serving on its own self-signed leaf, so the site is up, the route works,
/// and every browser refuses it — while the deploy that caused it reported success.
/// </summary>
public class SslStreamCertificateInspectorTests
{
    private static X509Certificate2 SelfSigned(
        string subjectName,
        string[]? alternativeNames = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subjectName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        if (alternativeNames is { Length: > 0 })
        {
            var builder = new SubjectAlternativeNameBuilder();
            foreach (var name in alternativeNames)
            {
                builder.AddDnsName(name);
            }

            request.CertificateExtensions.Add(builder.Build());
        }

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(89));
    }

    // Traefik's fallback leaf. Recognising it by name is what separates "the certificate has not
    // been issued" from "something else is answering TLS here" -- and only the first of those is
    // worth telling the user to fix their DNS over.
    [Fact]
    public void Describe_ReportsProxyDefault_ForTheTraefikFallbackCertificate()
    {
        using var certificate = SelfSigned("TRAEFIK DEFAULT CERT");

        var inspection = SslStreamCertificateInspector.Describe("app.example.com", certificate);

        Assert.Equal(CertificateOutcome.ProxyDefault, inspection.Outcome);
        Assert.False(inspection.IsIssued);
        Assert.False(inspection.IsInconclusive);
        Assert.Contains(inspection.Findings, f =>
            f.Contains("ran and failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Describe_ReportsSelfSigned_ForAnyOtherUnchainedCertificate()
    {
        using var certificate = SelfSigned("app.example.com", ["app.example.com"]);

        var inspection = SslStreamCertificateInspector.Describe("app.example.com", certificate);

        Assert.Equal(CertificateOutcome.SelfSigned, inspection.Outcome);
    }

    [Fact]
    public void Describe_ReadsTheSubjectAlternativeNames()
    {
        using var certificate = SelfSigned("app.example.com", ["app.example.com", "www.example.com"]);

        var inspection = SslStreamCertificateInspector.Describe("app.example.com", certificate);

        Assert.Contains("app.example.com", inspection.SubjectAlternativeNames);
        Assert.Contains("www.example.com", inspection.SubjectAlternativeNames);
    }

    [Fact]
    public void Describe_AlwaysPopulatesFindings()
    {
        using var certificate = SelfSigned("app.example.com", ["app.example.com"]);

        Assert.NotEmpty(SslStreamCertificateInspector.Describe("app.example.com", certificate).Findings);
    }

    [Theory]
    [InlineData("app.example.com", "app.example.com", true)]
    [InlineData("app.example.com", "APP.EXAMPLE.COM", true)]
    [InlineData("app.example.com", "*.example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    // A wildcard covers exactly one label. Treating it as covering any depth would let a
    // certificate for *.example.com pass for a.b.example.com, which no browser accepts.
    [InlineData("a.b.example.com", "*.example.com", false)]
    [InlineData("app.example.com", "app.example.org", false)]
    [InlineData("app.example.com", "other.example.com", false)]
    public void MatchesHostname_FollowsTheWildcardRuleBrowsersUse(
        string hostname, string candidate, bool expected)
    {
        Assert.Equal(expected, SslStreamCertificateInspector.MatchesHostname(hostname, candidate));
    }

    [Fact]
    public async Task InspectAsync_IsInconclusive_WhenNothingIsListening()
    {
        var inspector = new SslStreamCertificateInspector(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SslStreamCertificateInspector>.Instance);

        // A name reserved by RFC 6761 as guaranteed not to resolve, so this reaches no network.
        var inspection = await inspector.InspectAsync("nothing.invalid", CancellationToken.None);

        Assert.Equal(CertificateOutcome.Unreachable, inspection.Outcome);
        Assert.True(inspection.IsInconclusive);
        Assert.NotEmpty(inspection.Findings);
    }
}
