using DeployAI.Api.Services;

namespace DeployAI.Tests.Services;

/// <summary>
/// A GitHub App's private key is shown once by GitHub and cannot be reissued, so DeployAI can
/// neither generate it nor read it back from anywhere. Without somewhere to keep it, every app that
/// needs one asks the user to paste it again — which the standing rule calls a gap.
/// </summary>
public class AccountSecretsStoreTests
{
    private const string Pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIE...\n-----END RSA PRIVATE KEY-----";

    [Fact]
    public void SurvivesARoundTrip()
    {
        var store = new AccountSecretsStore();
        store.Set(AccountSecretsStore.GitHubAppId, "1234567");
        store.Set(AccountSecretsStore.GitHubAppPrivateKey, Pem);

        var reloaded = AccountSecretsStore.Parse(store.Serialize());

        Assert.Equal("1234567", reloaded.Find(AccountSecretsStore.GitHubAppId));
        Assert.Equal(Pem, reloaded.Find(AccountSecretsStore.GitHubAppPrivateKey));
    }

    [Fact]
    public void FillsAnyAppsPrefixedVariableFromOneSavedValue()
    {
        // The point of saving it on the account rather than the project: the second app that needs
        // a GitHub App key must not have to be told about it separately.
        var store = new AccountSecretsStore();
        store.Set(AccountSecretsStore.GitHubAppId, "1234567");
        store.Set(AccountSecretsStore.GitHubAppPrivateKey, Pem);

        var suggestions = store.SuggestFor([
            "MIRQAB_GITHUB_APP_ID",
            "MIRQAB_GITHUB_PRIVATE_KEY",
            "YEMENHUB_GITHUB_PRIVATE_KEY"
        ]);

        Assert.Equal("1234567", suggestions["MIRQAB_GITHUB_APP_ID"].Value);
        Assert.Equal(Pem, suggestions["MIRQAB_GITHUB_PRIVATE_KEY"].Value);
        Assert.Equal(Pem, suggestions["YEMENHUB_GITHUB_PRIVATE_KEY"].Value);
        Assert.Equal("the GitHub App saved on your account", suggestions["MIRQAB_GITHUB_APP_ID"].Source);
    }

    [Fact]
    public void SuggestsNothingForUnrelatedSettings()
    {
        var store = new AccountSecretsStore();
        store.Set(AccountSecretsStore.GitHubAppPrivateKey, Pem);

        var suggestions = store.SuggestFor(["MIRQAB_ENCRYPTION_KEY", "JWT_PRIVATE_KEY", "DATABASE_URL"]);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void ClearingAFieldForgetsIt()
    {
        // How a user removes a key they no longer want DeployAI holding.
        var store = new AccountSecretsStore();
        store.Set(AccountSecretsStore.GitHubAppPrivateKey, Pem);
        store.Set(AccountSecretsStore.GitHubAppPrivateKey, "");

        Assert.Null(store.Find(AccountSecretsStore.GitHubAppPrivateKey));
        Assert.Empty(store.NamesSet());
        Assert.Empty(store.SuggestFor(["MIRQAB_GITHUB_PRIVATE_KEY"]));
    }

    [Fact]
    public void ListsWhichNamesAreSetWithoutTheirValues()
    {
        var store = new AccountSecretsStore();
        store.Set(AccountSecretsStore.GitHubAppPrivateKey, Pem);

        var set = store.NamesSet();

        Assert.Contains(AccountSecretsStore.GitHubAppPrivateKey, set);
        Assert.DoesNotContain(AccountSecretsStore.GitHubAppId, set);
    }

    [Fact]
    public void TreatsAnUnreadableBlobAsEmptyRatherThanFailing()
    {
        Assert.Empty(AccountSecretsStore.Parse("not json").NamesSet());
        Assert.Empty(AccountSecretsStore.Parse(null).NamesSet());
    }
}
