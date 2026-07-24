using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

public class ObjectStorageConnectionImportTests
{
    [Fact]
    public void FromEnvironment_ReadsComposeStyleKeys()
    {
        var result = ObjectStorageConnectionImport.FromEnvironment(
        [
            ("STORAGE_ENDPOINT", "https://fsn1.your-objectstorage.com"),
            ("STORAGE_REGION", "fsn1"),
            ("STORAGE_BUCKET", "yemeni-breeze"),
            ("STORAGE_ACCESS_KEY", "AK123"),
            ("STORAGE_SECRET_KEY", "SK456")
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("yemeni-breeze", result.Connection!.Bucket);
        Assert.Equal("fsn1", result.Connection.Region);
    }

    // Coolify lists a compose app's variables twice — once at compose level and once per
    // service — and only one copy carries the value. Taking the first match rather than the
    // first non-blank match silently imports the empty twin.
    [Fact]
    public void FromEnvironment_SkipsTheBlankDuplicate()
    {
        var result = ObjectStorageConnectionImport.FromEnvironment(
        [
            ("STORAGE_ENDPOINT", ""),
            ("STORAGE_ENDPOINT", "https://fsn1.your-objectstorage.com"),
            ("STORAGE_BUCKET", ""),
            ("STORAGE_BUCKET", "yemeni-breeze"),
            ("STORAGE_ACCESS_KEY", "AK123"),
            ("STORAGE_ACCESS_KEY", ""),
            ("STORAGE_SECRET_KEY", "SK456"),
            ("STORAGE_SECRET_KEY", "")
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("https://fsn1.your-objectstorage.com", result.Connection!.Endpoint);
        Assert.Equal("yemeni-breeze", result.Connection.Bucket);
    }

    [Theory]
    [InlineData("Storage__Endpoint", "Storage__Bucket", "Storage__AccessKey", "Storage__SecretKey")]
    [InlineData("S3_ENDPOINT", "S3_BUCKET", "S3_ACCESS_KEY", "S3_SECRET_KEY")]
    [InlineData("AWS_ENDPOINT_URL", "AWS_BUCKET", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY")]
    public void FromEnvironment_RecognisesOtherNamingConventions(
        string endpointKey, string bucketKey, string accessKey, string secretKey)
    {
        var result = ObjectStorageConnectionImport.FromEnvironment(
        [
            (endpointKey, "https://endpoint.example.com"),
            (bucketKey, "media"),
            (accessKey, "AK"),
            (secretKey, "SK")
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("media", result.Connection!.Bucket);
    }

    // Region only signs the request; a connection without one still works.
    [Fact]
    public void FromEnvironment_SucceedsWithoutARegion()
    {
        var result = ObjectStorageConnectionImport.FromEnvironment(
        [
            ("STORAGE_ENDPOINT", "https://endpoint.example.com"),
            ("STORAGE_BUCKET", "media"),
            ("STORAGE_ACCESS_KEY", "AK"),
            ("STORAGE_SECRET_KEY", "SK")
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.Connection!.Region);
    }

    [Fact]
    public void FromEnvironment_NamesWhatIsMissing()
    {
        var result = ObjectStorageConnectionImport.FromEnvironment(
        [
            ("STORAGE_ENDPOINT", "https://endpoint.example.com"),
            ("STORAGE_BUCKET", "media")
        ]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Connection);
        Assert.Contains("STORAGE_ACCESS_KEY", result.MissingKeys);
        Assert.Contains("STORAGE_SECRET_KEY", result.MissingKeys);
        Assert.DoesNotContain("STORAGE_ENDPOINT", result.MissingKeys);
    }

    [Fact]
    public void FromEnvironment_TreatsAnAppWithNoStorageAsIncomplete()
    {
        var result = ObjectStorageConnectionImport.FromEnvironment(
        [
            ("JWT_KEY", "secret"),
            ("ADMIN_EMAIL", "admin@example.com")
        ]);

        Assert.False(result.Succeeded);
        Assert.Equal(4, result.MissingKeys.Count);
    }

    [Fact]
    public void FromEnvironment_TrimsSurroundingWhitespace()
    {
        var result = ObjectStorageConnectionImport.FromEnvironment(
        [
            ("STORAGE_ENDPOINT", "  https://endpoint.example.com  "),
            ("STORAGE_BUCKET", "media"),
            ("STORAGE_ACCESS_KEY", "AK"),
            ("STORAGE_SECRET_KEY", "SK")
        ]);

        Assert.Equal("https://endpoint.example.com", result.Connection!.Endpoint);
    }
}
