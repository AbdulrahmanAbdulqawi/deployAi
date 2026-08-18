using DeployAI.Core.Domains;

namespace DeployAI.Tests.Domains;

/// <summary>
/// Normalisation happens once so that the resolver, the provider API, the certificate's name list
/// and the uniqueness index all compare the same spelling. Any of those disagreeing produces a
/// mismatch with no visible cause.
/// </summary>
public class DomainNameRulesTests
{
    private static string Normalize(string typed)
    {
        Assert.True(DomainNameRules.TryNormalize(typed, out var hostname, out var rejection),
            rejection?.Reason);
        return hostname;
    }

    private static string Reject(string typed)
    {
        Assert.False(DomainNameRules.TryNormalize(typed, out _, out var rejection));
        Assert.NotNull(rejection);
        return rejection.Reason;
    }

    [Theory]
    [InlineData("app.example.com", "app.example.com")]
    [InlineData("  app.example.com  ", "app.example.com")]
    [InlineData("APP.Example.COM", "app.example.com")]
    [InlineData("https://app.example.com", "app.example.com")]
    [InlineData("http://app.example.com/", "app.example.com")]
    [InlineData("app.example.com.", "app.example.com")]
    [InlineData("https://app.example.com:8443/dashboard?x=1", "app.example.com")]
    public void TryNormalize_ReducesEveryFormOfTheSameNameToOne(string typed, string expected)
    {
        Assert.Equal(expected, Normalize(typed));
    }

    // A unicode domain compared against its own punycode form mismatches at every boundary it
    // crosses, and each of those failures looks like a different problem.
    [Fact]
    public void TryNormalize_ConvertsAnInternationalNameToPunycode()
    {
        Assert.Equal("xn--mgbh0fb.example.com", Normalize("مثال.example.com"));
    }

    [Fact]
    public void TryNormalize_RejectsAWildcard_BecauseItsCertificateCouldNeverIssue()
    {
        Assert.Contains("Wildcard", Reject("*.example.com"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalize_RejectsANameWithNoDomainEnding()
    {
        Assert.Contains("domain ending", Reject("localhost"), StringComparison.OrdinalIgnoreCase);
    }

    // The generated address is not a domain the user chose, and asking them to verify DNS for it
    // would be asking them to verify a record that does not exist and never needs to.
    [Fact]
    public void TryNormalize_RejectsTheServersOwnTemporaryAddress()
    {
        Assert.Contains("temporary address",
            Reject("app-uuid.46.225.80.188.sslip.io"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    public void TryNormalize_RejectsAnEmptyName(string typed)
    {
        Assert.NotEmpty(Reject(typed));
    }

    [Fact]
    public void TryNormalize_RejectsANameLongerThanDnsAllows()
    {
        var tooLong = string.Join('.', Enumerable.Repeat("abcdefghij", 26)) + ".com";

        Assert.Contains("too long", Reject(tooLong), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("app.example.com", "example.com", "app")]
    [InlineData("example.com", "example.com", "@")]
    [InlineData("a.b.example.com", "example.com", "a.b")]
    public void RecordNameWithin_GivesTheNameToTypeIntoADnsDashboard(
        string hostname, string zone, string expected)
    {
        Assert.Equal(expected, DomainNameRules.RecordNameWithin(hostname, zone));
    }

    [Theory]
    [InlineData("x.46.225.80.188.sslip.io", true)]
    [InlineData("x.46.225.80.188.nip.io", true)]
    [InlineData("app.example.com", false)]
    public void IsSslip_RecognisesTheGeneratedAddresses(string hostname, bool expected)
    {
        Assert.Equal(expected, DomainNameRules.IsSslip(hostname));
    }
}
