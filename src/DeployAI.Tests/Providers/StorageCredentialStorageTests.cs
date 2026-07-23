using DeployAI.Core.Providers;

namespace DeployAI.Tests.Providers;

public class StorageCredentialStorageTests
{
    private const string Endpoint = "https://hel1.your-objectstorage.com";

    [Fact]
    public void Serialize_ThenTryParse_RoundTrips()
    {
        var token = StorageCredentialStorage.Serialize(Endpoint, "hel1", "AKIAEXAMPLE", "secret-key");

        var parsed = StorageCredentialStorage.TryParse(token);

        Assert.NotNull(parsed);
        Assert.Equal(Endpoint, parsed!.Endpoint);
        Assert.Equal("hel1", parsed.Region);
        Assert.Equal("AKIAEXAMPLE", parsed.AccessKey);
        Assert.Equal("secret-key", parsed.SecretKey);
        Assert.Equal(StorageCredentialStorage.StorageKind, parsed.Kind);
    }

    [Fact]
    public void Serialize_TrimsValuesAndNormalizesEndpoint()
    {
        var token = StorageCredentialStorage.Serialize(
            "  https://hel1.your-objectstorage.com/some/path/  ", " hel1 ", " key ", " secret ");

        var parsed = StorageCredentialStorage.TryParse(token);

        Assert.NotNull(parsed);
        Assert.Equal(Endpoint, parsed!.Endpoint); // path stripped, trailing slash removed
        Assert.Equal("hel1", parsed.Region);
        Assert.Equal("key", parsed.AccessKey);
        Assert.Equal("secret", parsed.SecretKey);
    }

    // The discriminator exists so the two JSON envelopes stored in the same
    // ProviderCredentials.Token column can never be mistaken for one another.

    [Fact]
    public void TryParse_ReturnsNull_ForCoolifyPayload()
    {
        var coolifyToken = CoolifyCredentialStorage.Serialize("https://coolify.example.com", "coolify-token");

        Assert.Null(StorageCredentialStorage.TryParse(coolifyToken));
    }

    [Fact]
    public void CoolifyTryParse_DoesNotYieldUsableCredentials_ForStoragePayload()
    {
        var storageToken = StorageCredentialStorage.Serialize(Endpoint, "hel1", "key", "secret");

        // Coolify's parser only sniffs for a leading '{', so it may deserialize this —
        // what matters is that it can never produce a usable instance URL or API token.
        var parsed = CoolifyCredentialStorage.TryParse(storageToken);

        Assert.True(parsed is null
            || (string.IsNullOrWhiteSpace(parsed.InstanceUrl) && string.IsNullOrWhiteSpace(parsed.ApiToken)));
    }

    [Fact]
    public void TryParse_ReturnsNull_WhenKindMissing()
    {
        const string noKind = """
        {"endpoint":"https://hel1.your-objectstorage.com","region":"hel1","accessKey":"k","secretKey":"s"}
        """;

        Assert.Null(StorageCredentialStorage.TryParse(noKind));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("{ this is not valid json")]
    public void TryParse_ReturnsNull_ForNonPayloads(string token)
    {
        Assert.Null(StorageCredentialStorage.TryParse(token));
    }

    [Fact]
    public void TryParse_ReturnsNull_WhenRequiredFieldsBlank()
    {
        const string missingSecret = """
        {"kind":"s3","endpoint":"https://hel1.your-objectstorage.com","region":"hel1","accessKey":"k","secretKey":""}
        """;

        Assert.Null(StorageCredentialStorage.TryParse(missingSecret));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://hel1.your-objectstorage.com")]
    public void NormalizeEndpoint_Rejects_InvalidEndpoints(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => StorageCredentialStorage.NormalizeEndpoint(endpoint));
    }
}
