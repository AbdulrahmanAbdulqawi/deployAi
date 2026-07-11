using DeployAI.Core.Providers;

namespace DeployAI.Tests.Providers;

public class CoolifyCredentialStorageTests
{
    [Fact]
    public void Serialize_NormalizesInstanceUrl()
    {
        var json = CoolifyCredentialStorage.Serialize("https://coolify.example.com/", "token-123");
        var payload = CoolifyCredentialStorage.TryParse(json);

        Assert.NotNull(payload);
        Assert.Equal("https://coolify.example.com", payload!.InstanceUrl);
        Assert.Equal("token-123", payload.ApiToken);
    }

    [Fact]
    public void TryParse_ReturnsNull_ForPlainToken()
    {
        Assert.Null(CoolifyCredentialStorage.TryParse("plain-token"));
    }

    [Fact]
    public void NormalizeInstanceUrl_RejectsInvalidUrl()
    {
        Assert.Throws<ArgumentException>(() => CoolifyCredentialStorage.NormalizeInstanceUrl("not-a-url"));
    }
}
