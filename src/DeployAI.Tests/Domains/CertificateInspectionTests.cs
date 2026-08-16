using DeployAI.Core.Domains;

namespace DeployAI.Tests.Domains;

/// <summary>
/// The certificate states a deployed domain can be in, and which of them mean "wait" rather than
/// "broken". The distinction that matters most is <see cref="CertificateOutcome.Unreachable"/>:
/// a handshake that never completed says nothing about the certificate, and reporting it as a
/// failure would condemn a site that is merely still starting.
/// </summary>
public class CertificateInspectionTests
{
    private static CertificateInspection Inspection(CertificateOutcome outcome) =>
        new("app.example.com", outcome, "issuer", "subject", null, null, [], ["finding"]);

    [Fact]
    public void IsInconclusive_OnlyWhenTheHandshakeNeverCompleted()
    {
        Assert.True(Inspection(CertificateOutcome.Unreachable).IsInconclusive);

        foreach (var outcome in Enum.GetValues<CertificateOutcome>()
                     .Where(o => o != CertificateOutcome.Unreachable))
        {
            Assert.False(Inspection(outcome).IsInconclusive);
        }
    }

    [Fact]
    public void IsIssued_OnlyForAValidCertificate()
    {
        Assert.True(Inspection(CertificateOutcome.Valid).IsIssued);

        foreach (var outcome in Enum.GetValues<CertificateOutcome>()
                     .Where(o => o != CertificateOutcome.Valid))
        {
            Assert.False(Inspection(outcome).IsIssued);
        }
    }

    // The proxy's own fallback certificate is not just "some invalid certificate" -- it is the
    // recognisable trace of an ACME challenge that ran and failed, which is a different problem
    // from a certificate that was never requested, and needs a different message.
    [Fact]
    public void ProxyDefault_IsDistinctFromAnOrdinarySelfSignedCertificate()
    {
        Assert.NotEqual(CertificateOutcome.SelfSigned, CertificateOutcome.ProxyDefault);
        Assert.False(Inspection(CertificateOutcome.ProxyDefault).IsIssued);
        Assert.False(Inspection(CertificateOutcome.ProxyDefault).IsInconclusive);
    }
}
