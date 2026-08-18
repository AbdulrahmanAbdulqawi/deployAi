using DeployAI.Core.Domains;

namespace DeployAI.Tests.Domains;

/// <summary>
/// Naming an app under DeployAI's own zone. This is the only path to a working HTTPS address that
/// asks nothing at all of someone who does not know what an A record is — today the alternative is
/// the server's generated sslip.io address, which is plain HTTP and unreadable.
/// </summary>
public class PlatformSubdomainTests
{
    private const string Zone = "apps.deployai.dev";

    [Theory]
    [InlineData("breeze", "breeze")]
    [InlineData("Yemeni Breeze", "yemeni-breeze")]
    [InlineData("  My  App  ", "my-app")]
    [InlineData("shop_v2", "shop-v2")]
    [InlineData("café", "caf")]
    [InlineData("--weird--name--", "weird-name")]
    public void Slugify_ProducesAValidDnsLabel(string projectName, string expected)
    {
        Assert.Equal(expected, PlatformSubdomain.Slugify(projectName));
    }

    [Fact]
    public void Slugify_StaysWithinTheLabelLengthLimit()
    {
        var slug = PlatformSubdomain.Slugify(new string('a', 200));

        Assert.True(slug.Length <= 63);
    }

    [Fact]
    public void TryBuild_NamesTheAppUnderThePlatformZone()
    {
        Assert.Equal($"breeze.{Zone}", PlatformSubdomain.TryBuild("Breeze", Zone));
    }

    // An offer that cannot be honoured must not be made: with no zone configured there is no
    // wildcard record, so the name would resolve to nothing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryBuild_IsNull_WhenNoPlatformZoneIsConfigured(string? zone)
    {
        Assert.Null(PlatformSubdomain.TryBuild("Breeze", zone));
    }

    [Fact]
    public void TryBuild_IsNull_WhenTheProjectNameHasNoUsableCharacters()
    {
        Assert.Null(PlatformSubdomain.TryBuild("!!!", Zone));
    }

    // Two projects called the same thing must not be handed the same hostname; the second would
    // take the first one's traffic.
    [Fact]
    public void TryBuild_AvoidsANameAlreadyInUse()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"breeze.{Zone}" };

        Assert.Equal($"breeze-2.{Zone}", PlatformSubdomain.TryBuild("Breeze", Zone, taken));
    }

    [Fact]
    public void TryBuild_KeepsCountingPastTheFirstCollision()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"breeze.{Zone}", $"breeze-2.{Zone}", $"breeze-3.{Zone}"
        };

        Assert.Equal($"breeze-4.{Zone}", PlatformSubdomain.TryBuild("Breeze", Zone, taken));
    }

    [Fact]
    public void TryBuild_NormalizesTheZone()
    {
        Assert.Equal($"breeze.{Zone}", PlatformSubdomain.TryBuild("Breeze", $".{Zone.ToUpperInvariant()}."));
    }

    // Whatever it builds has to survive the same normalisation every other domain goes through.
    [Fact]
    public void TryBuild_ProducesANameTheDomainRulesAccept()
    {
        var built = PlatformSubdomain.TryBuild("Yemeni Breeze", Zone)!;

        Assert.True(DomainNameRules.TryNormalize(built, out var normalized, out _));
        Assert.Equal(built, normalized);
    }
}
