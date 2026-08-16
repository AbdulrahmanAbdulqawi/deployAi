using DeployAI.Core.Domains;

namespace DeployAI.Tests.Domains;

/// <summary>
/// The distinctions this type exists to keep. Every one of them is a case where reporting the
/// wrong answer sends a non-technical user to change something that was never broken, or lets a
/// deploy proceed into a certificate request that cannot succeed.
/// </summary>
public class DnsCheckResultTests
{
    private const string ServerIp = "46.225.80.188";

    private static DnsCheckResult Combine(params DnsObservation[] observations) =>
        DnsObservationCombiner.Combine("app.example.com", ServerIp, observations);

    private static DnsObservation Answered(
        string resolver,
        string[] addresses,
        string? alias = null,
        string[]? authorities = null) =>
        new(resolver, Answered: true, addresses, alias, authorities);

    [Fact]
    public void Combine_ReportsMatches_WhenTheRecordPointsAtTheServer()
    {
        var result = Combine(
            Answered("1.1.1.1", [ServerIp]),
            Answered("8.8.8.8", [ServerIp]));

        Assert.Equal(DnsOutcome.Matches, result.Outcome);
        Assert.True(result.PointsAtTarget);
        Assert.False(result.IsInconclusive);
    }

    [Fact]
    public void Combine_ReportsMismatch_WhenTheRecordPointsSomewhereElse()
    {
        var result = Combine(Answered("1.1.1.1", ["203.0.113.10"]));

        Assert.Equal(DnsOutcome.Mismatch, result.Outcome);
        Assert.False(result.PointsAtTarget);
    }

    // A resolver saying "this name has no address" has told us something. A resolver that never
    // replied has not. Collapsing the two turns a network blip into "your DNS is wrong" -- a claim
    // the user cannot check and did not earn.
    [Fact]
    public void Combine_ReportsNotFound_WhenAResolverAnsweredWithNoRecords()
    {
        var result = Combine(Answered("1.1.1.1", []));

        Assert.Equal(DnsOutcome.NotFound, result.Outcome);
        Assert.False(result.IsInconclusive);
    }

    [Fact]
    public void Combine_IsInconclusive_WhenNoResolverAnswered()
    {
        var result = Combine(
            DnsObservation.Unreachable("1.1.1.1", "timed out"),
            DnsObservation.Unreachable("8.8.8.8", "server failure"));

        Assert.True(result.IsInconclusive);
        Assert.Equal(DnsOutcome.Inconclusive, result.Outcome);
        Assert.False(result.PointsAtTarget);
    }

    [Fact]
    public void Combine_IsNotInconclusive_WhenOnlySomeResolversFailed()
    {
        var result = Combine(
            DnsObservation.Unreachable("1.1.1.1", "timed out"),
            Answered("8.8.8.8", [ServerIp]));

        Assert.False(result.IsInconclusive);
        Assert.True(result.PointsAtTarget);
    }

    // Coolify routes on an A record. A CNAME resolves perfectly well and still will not do, so the
    // shape has to survive separately from the address it happens to reach.
    [Fact]
    public void Combine_ReportsTheAliasTarget_BecauseCoolifyNeedsAnARecord()
    {
        var result = Combine(Answered("1.1.1.1", [], alias: "app.vercel.app"));

        Assert.Equal("app.vercel.app", result.AliasTarget);
        Assert.Equal(DnsOutcome.Mismatch, result.Outcome);
        Assert.Contains(result.Findings, f => f.Contains("CNAME", StringComparison.OrdinalIgnoreCase));
    }

    // A proxied record is not a wrong record -- it is a record whose proxy breaks the HTTP-01
    // challenge. Telling the user "this does not point at your server" would send them to change
    // an address that is already correct.
    [Fact]
    public void Combine_ReportsProxiedByCdn_RatherThanAPlainMismatch()
    {
        var result = Combine(Answered("1.1.1.1", ["104.16.132.229", "172.67.74.10"]));

        Assert.True(result.IsProxiedByCdn);
        Assert.Contains(result.Findings, f => f.Contains("DNS-only", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Combine_DoesNotClaimProxied_ForAnOrdinaryWrongAddress()
    {
        Assert.False(Combine(Answered("1.1.1.1", ["203.0.113.10"])).IsProxiedByCdn);
    }

    [Fact]
    public void Combine_DoesNotClaimProxied_WhenOnlySomeAddressesAreCdnEdges()
    {
        Assert.False(Combine(Answered("1.1.1.1", ["104.16.132.229", "203.0.113.10"])).IsProxiedByCdn);
    }

    // A CAA record that excludes Let's Encrypt makes every certificate attempt fail. Catching it
    // before the first attempt is the difference between one clear message and five spent
    // validations an hour.
    [Fact]
    public void Combine_BlocksLetsEncrypt_WhenCaaNamesSomeoneElse()
    {
        var result = Combine(Answered("1.1.1.1", [ServerIp], authorities: ["digicert.com"]));

        Assert.True(result.BlocksLetsEncrypt);
        Assert.Contains(result.Findings, f => f.Contains("CAA", StringComparison.Ordinal));
    }

    [Fact]
    public void Combine_DoesNotBlockLetsEncrypt_WhenCaaIncludesIt()
    {
        var result = Combine(
            Answered("1.1.1.1", [ServerIp], authorities: ["digicert.com", "letsencrypt.org"]));

        Assert.False(result.BlocksLetsEncrypt);
    }

    // No CAA published means every authority is allowed. That is the common case, and reading it
    // as a block would refuse almost every domain.
    [Fact]
    public void Combine_DoesNotBlockLetsEncrypt_WhenNoCaaIsPublished()
    {
        Assert.False(Combine(Answered("1.1.1.1", [ServerIp])).BlocksLetsEncrypt);
    }

    [Fact]
    public void Combine_AlwaysRecordsEveryResolverItAsked()
    {
        var result = Combine(
            DnsObservation.Unreachable("1.1.1.1", "timed out"),
            Answered("8.8.8.8", [ServerIp]));

        Assert.Equal(["1.1.1.1", "8.8.8.8"], result.ResolversQueried);
        Assert.Equal(["8.8.8.8"], result.ResolversAnswered);
    }

    // A result that explains nothing reads exactly like a check that never ran.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Combine_AlwaysPopulatesFindings(bool answered)
    {
        var result = Combine(answered
            ? Answered("1.1.1.1", [ServerIp])
            : DnsObservation.Unreachable("1.1.1.1", "timed out"));

        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public void Combine_SaysItCouldNotLook_RatherThanBlamingTheDomain()
    {
        var result = Combine(DnsObservation.Unreachable("1.1.1.1", "timed out"));

        Assert.Contains(result.Findings, f =>
            f.Contains("Could not check", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Findings, f =>
            f.Contains("has no address records", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Combine_MergesAddressesSeenAcrossResolvers()
    {
        var result = Combine(
            Answered("1.1.1.1", ["203.0.113.10"]),
            Answered("8.8.8.8", [ServerIp]));

        Assert.Equal(["203.0.113.10", ServerIp], result.ObservedAddresses);
        Assert.True(result.PointsAtTarget);
    }
}
