using DeployAI.Providers.Coolify;

namespace DeployAI.Tests.Providers;

public class CoolifyApiSupportTests
{
    [Fact]
    // Mirqab's compose app never got a domain at creation -- Coolify rejects one before the
    // first deploy -- so its top-level fqdn was empty forever, with nothing to read back. This is
    // the fallback: Coolify's own convention for a domain-free app, derived from the instance
    // address DeployAI already has, not guessed.
    public void TryBuildSslipDomain_EncodesTheServerIpFromTheInstanceUrl()
    {
        var domain = CoolifyApiSupport.TryBuildSslipDomain("http://46.225.80.188:8000", "co46gl1cdvzs3fbd5z05y5ap");

        Assert.Equal("http://co46gl1cdvzs3fbd5z05y5ap.46.225.80.188.sslip.io", domain);
    }

    [Fact]
    public void TryBuildSslipDomain_WorksFromAnHttpsInstanceUrlToo()
    {
        var domain = CoolifyApiSupport.TryBuildSslipDomain("https://46.225.80.188:8443", "app-uuid");

        Assert.Equal("http://app-uuid.46.225.80.188.sslip.io", domain);
    }

    [Fact]
    // A hostname-addressed instance ("coolify.example.com") has no IP to encode into an sslip.io
    // domain -- guessing one would be wrong, not just incomplete, so this returns null rather
    // than fabricate a domain nobody asked for.
    public void TryBuildSslipDomain_ReturnsNull_WhenTheInstanceIsAddressedByAHostnameNotAnIp()
    {
        var domain = CoolifyApiSupport.TryBuildSslipDomain("https://coolify.example.com", "app-uuid");

        Assert.Null(domain);
    }

    [Fact]
    public void TryBuildSslipDomain_ReturnsNull_ForAnUnparsableInstanceUrl()
    {
        var domain = CoolifyApiSupport.TryBuildSslipDomain("not-a-url", "app-uuid");

        Assert.Null(domain);
    }
}
