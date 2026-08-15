using DeployAI.Api.Services;

namespace DeployAI.Tests.Services;

/// <summary>
/// Mirqab's deploy stopped on four empty boxes, two of which DeployAI was already holding: the
/// Coolify URL and the Coolify API token live in the connection that was about to deploy it. The
/// standing rule is that no credential should have to be pasted, and a value the user already gave
/// DeployAI is the easiest case of it to honour.
/// </summary>
public class ConnectionEnvSuggestionsTests
{
    private const string Url = "http://46.225.80.188:8000";
    private const string Token = "3|abcdef";

    [Fact]
    public void FillsAPrefixedCoolifyUrl()
    {
        // Apps prefix these with their own name, so an exact-name match would never fire.
        var suggestions = ConnectionEnvSuggestions.ForCoolify(Url, Token, ["MIRQAB_COOLIFY_URL"]);

        Assert.Equal(Url, suggestions["MIRQAB_COOLIFY_URL"].Value);
        Assert.Equal("your Coolify connection", suggestions["MIRQAB_COOLIFY_URL"].Source);
        // A URL grants nothing, so it must not carry a warning — noise is what teaches people to
        // skip the line that matters.
        Assert.Null(suggestions["MIRQAB_COOLIFY_URL"].Exposure);
    }

    [Fact]
    public void OffersTheTokenWithWhatAcceptingItGrants()
    {
        var suggestions = ConnectionEnvSuggestions.ForCoolify(Url, Token, ["MIRQAB_COOLIFY_TOKEN"]);

        Assert.Equal(Token, suggestions["MIRQAB_COOLIFY_TOKEN"].Value);
        Assert.NotNull(suggestions["MIRQAB_COOLIFY_TOKEN"].Exposure);
        Assert.Contains("every application", suggestions["MIRQAB_COOLIFY_TOKEN"].Exposure!, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotHandTheCoolifyTokenToEveryTokenShapedSetting()
    {
        // The failure this guard exists for: an app's own JWT or webhook token prefilled with a
        // credential that manages the whole server, looking authoritative because DeployAI wrote it.
        var suggestions = ConnectionEnvSuggestions.ForCoolify(
            Url, Token, ["MIRQAB_JWT_TOKEN", "GITHUB_TOKEN", "API_TOKEN", "TOKEN"]);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void SuggestsNothingWhenTheConnectionHoldsNothing()
    {
        Assert.Empty(ConnectionEnvSuggestions.ForCoolify(null, null, ["MIRQAB_COOLIFY_URL", "MIRQAB_COOLIFY_TOKEN"]));
        Assert.Empty(ConnectionEnvSuggestions.ForCoolify("  ", "  ", ["MIRQAB_COOLIFY_URL"]));
    }

    [Fact]
    public void FillsTheGitHubAppIdFromTheSelectedApp()
    {
        var suggestions = ConnectionEnvSuggestions.ForGitHubApp("1234567", ["MIRQAB_GITHUB_APP_ID"]);

        Assert.Equal("1234567", suggestions["MIRQAB_GITHUB_APP_ID"].Value);
        Assert.Null(suggestions["MIRQAB_GITHUB_APP_ID"].Exposure);
    }

    [Fact]
    public void NeverOffersAGitHubAppPrivateKey()
    {
        // GitHub shows a private key once and cannot reissue it, so there is nothing to suggest.
        // Leaving the box empty is the honest answer; a generated value would boot and fail.
        var suggestions = ConnectionEnvSuggestions.ForGitHubApp("1234567", ["MIRQAB_GITHUB_PRIVATE_KEY"]);

        Assert.DoesNotContain("MIRQAB_GITHUB_PRIVATE_KEY", suggestions.Keys);
    }
}
