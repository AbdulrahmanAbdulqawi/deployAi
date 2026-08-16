using DeployAI.Core.Domains;

namespace DeployAI.Tests.Domains;

/// <summary>
/// The rules that decide how far a domain has got. The one that matters most is
/// <see cref="DomainTransitions.MayRequestCertificate"/>: writing an https:// domain to the proxy
/// before DNS resolves makes it attempt a certificate that cannot succeed, which spends one of
/// Let's Encrypt's five failed validations an hour and leaves a self-signed certificate serving
/// behind a deploy that reported success.
/// </summary>
public class DomainTransitionsTests
{
    private const string ServerIp = "46.225.80.188";

    private static DnsCheckResult Dns(
        string[] addresses,
        bool answered = true,
        string[]? authorities = null) =>
        DnsObservationCombiner.Combine(
            "app.example.com",
            ServerIp,
            [answered
                ? new DnsObservation("1.1.1.1", true, addresses, null, authorities)
                : DnsObservation.Unreachable("1.1.1.1", "timed out")]);

    private static CertificateInspection Certificate(CertificateOutcome outcome) =>
        new("app.example.com", outcome, "issuer", "subject", null, null, [], ["finding"]);

    [Fact]
    public void AfterDnsCheck_AdvancesToVerified_WhenTheRecordPointsHere()
    {
        var transition = DomainTransitions.AfterDnsCheck(Dns([ServerIp]), false, null);

        Assert.Equal(DomainLifecycleState.DnsVerified, transition.State);
        Assert.Equal(DomainLifecycleState.DnsVerified, transition.ConclusiveStatus);
    }

    [Fact]
    public void AfterDnsCheck_KeepsWaiting_WhileTheRecordHasNotAppeared()
    {
        var transition = DomainTransitions.AfterDnsCheck(Dns([]), deadlinePassed: false, null);

        Assert.Equal(DomainLifecycleState.DnsPending, transition.State);
        Assert.False(transition.IsTerminal);
    }

    // A domain that was checked and is wrong gives the user something to do. A domain that could
    // never be checked does not, and telling them their DNS is broken would be a claim we never
    // made an observation to support.
    [Fact]
    public void AfterDnsCheck_AtTheDeadline_IsUnverifiable_WhenNoCheckEverConcluded()
    {
        var transition = DomainTransitions.AfterDnsCheck(
            Dns([], answered: false), deadlinePassed: true, lastConclusiveState: null);

        Assert.Equal(DomainLifecycleState.DnsUnverifiable, transition.State);
        Assert.True(transition.IsTerminal);
        Assert.Contains("Nothing is known to be wrong", transition.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AfterDnsCheck_AtTheDeadline_IsFailed_WhenAnEarlierCheckDidConclude()
    {
        var transition = DomainTransitions.AfterDnsCheck(
            Dns([], answered: false),
            deadlinePassed: true,
            lastConclusiveState: DomainLifecycleState.DnsFailed);

        Assert.Equal(DomainLifecycleState.DnsFailed, transition.State);
        Assert.True(transition.IsTerminal);
    }

    [Fact]
    public void AfterDnsCheck_AtTheDeadline_IsFailed_WhenTheRecordIsConclusivelyWrong()
    {
        var transition = DomainTransitions.AfterDnsCheck(
            Dns(["203.0.113.10"]), deadlinePassed: true, null);

        Assert.Equal(DomainLifecycleState.DnsFailed, transition.State);
        Assert.True(transition.IsTerminal);
    }

    // Every certificate attempt against this domain will fail, so waiting only spends the
    // rate-limit budget to arrive at the same answer later.
    [Fact]
    public void AfterDnsCheck_FailsImmediately_WhenCaaExcludesLetsEncrypt()
    {
        var transition = DomainTransitions.AfterDnsCheck(
            Dns([ServerIp], authorities: ["digicert.com"]), deadlinePassed: false, null);

        Assert.Equal(DomainLifecycleState.DnsFailed, transition.State);
        Assert.True(transition.IsTerminal);
        Assert.Contains("CAA", transition.Message, StringComparison.Ordinal);
    }

    // An inconclusive check must not be read as a CAA block: no answer means no CAA was seen, not
    // that none exists.
    [Fact]
    public void AfterDnsCheck_DoesNotClaimACaaBlock_WhenNothingAnswered()
    {
        var transition = DomainTransitions.AfterDnsCheck(
            Dns([], answered: false), deadlinePassed: false, null);

        Assert.Equal(DomainLifecycleState.DnsPending, transition.State);
    }

    [Fact]
    public void AfterCertificateCheck_GoesActive_OnAValidCertificate()
    {
        var transition = DomainTransitions.AfterCertificateCheck(
            Certificate(CertificateOutcome.Valid), deadlinePassed: false);

        Assert.Equal(DomainLifecycleState.Active, transition.State);
        Assert.True(transition.IsTerminal);
    }

    [Theory]
    [InlineData(CertificateOutcome.ProxyDefault)]
    [InlineData(CertificateOutcome.SelfSigned)]
    [InlineData(CertificateOutcome.Unreachable)]
    public void AfterCertificateCheck_KeepsWaiting_BeforeTheDeadline(CertificateOutcome outcome)
    {
        var transition = DomainTransitions.AfterCertificateCheck(
            Certificate(outcome), deadlinePassed: false);

        Assert.Equal(DomainLifecycleState.CertificatePending, transition.State);
        Assert.False(transition.IsTerminal);
    }

    // The proxy serving its own fallback certificate is evidence the challenge ran and failed.
    // A handshake that never completed is not evidence of anything.
    [Fact]
    public void AfterCertificateCheck_AtTheDeadline_SeparatesAFailedIssuanceFromAnUnreachableHost()
    {
        Assert.Equal(
            DomainLifecycleState.CertificateFailed,
            DomainTransitions.AfterCertificateCheck(
                Certificate(CertificateOutcome.ProxyDefault), deadlinePassed: true).State);

        Assert.Equal(
            DomainLifecycleState.CertificateUnverifiable,
            DomainTransitions.AfterCertificateCheck(
                Certificate(CertificateOutcome.Unreachable), deadlinePassed: true).State);
    }

    // The gate the whole design rests on.
    [Theory]
    [InlineData(DomainLifecycleState.Pending, false)]
    [InlineData(DomainLifecycleState.DnsPending, false)]
    [InlineData(DomainLifecycleState.DnsFailed, false)]
    [InlineData(DomainLifecycleState.DnsUnverifiable, false)]
    [InlineData(DomainLifecycleState.Conflicted, false)]
    [InlineData(DomainLifecycleState.Retired, false)]
    [InlineData(DomainLifecycleState.DnsVerified, true)]
    [InlineData(DomainLifecycleState.Assigned, true)]
    [InlineData(DomainLifecycleState.CertificatePending, true)]
    [InlineData(DomainLifecycleState.Active, true)]
    public void MayRequestCertificate_OnlyOnceDnsHasBeenProven(
        DomainLifecycleState state, bool expected)
    {
        Assert.Equal(expected, DomainTransitions.MayRequestCertificate(state));
    }

    // A state added later without a decision about the gate would otherwise default to allowed.
    [Fact]
    public void MayRequestCertificate_RefusesEveryStateBeforeDnsIsVerified()
    {
        foreach (var state in Enum.GetValues<DomainLifecycleState>()
                     .Where(s => s < DomainLifecycleState.DnsVerified))
        {
            Assert.False(DomainTransitions.MayRequestCertificate(state), state.ToString());
        }
    }

    [Fact]
    public void EveryTransition_CarriesAMessage()
    {
        DomainTransition[] transitions =
        [
            DomainTransitions.AfterDnsCheck(Dns([ServerIp]), false, null),
            DomainTransitions.AfterDnsCheck(Dns([]), false, null),
            DomainTransitions.AfterDnsCheck(Dns([], answered: false), true, null),
            DomainTransitions.AfterDnsCheck(Dns(["203.0.113.10"]), true, null),
            DomainTransitions.AfterCertificateCheck(Certificate(CertificateOutcome.Valid), false),
            DomainTransitions.AfterCertificateCheck(Certificate(CertificateOutcome.ProxyDefault), true),
            DomainTransitions.AfterCertificateCheck(Certificate(CertificateOutcome.Unreachable), true)
        ];

        Assert.All(transitions, t => Assert.False(string.IsNullOrWhiteSpace(t.Message)));
    }
}
