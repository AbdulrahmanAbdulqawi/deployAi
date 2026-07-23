using System.Net;
using Amazon.Runtime;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Providers.HetznerStorage;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

public class HetznerStorageProviderContractTests
{
    private const string Endpoint = "https://hel1.your-objectstorage.com";
    private const string Bucket = "yemeni-breeze-media";

    private static readonly ProviderCredentials Credentials =
        new(StorageCredentialStorage.Serialize(Endpoint, "hel1", "access-key", "secret-key"));

    private static HetznerStorageProvider CreateProvider(MockHttpMessageHandler handler) =>
        new(new MockS3HttpClientFactory(handler));

    [Fact]
    public async Task ValidateCredentialsAsync_ReturnsTrue_WhenListBucketsSucceeds()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Endpoint}/")
            .Respond(HttpStatusCode.OK, "application/xml", ListBucketsXml);

        var provider = CreateProvider(handler);

        Assert.True(await provider.ValidateCredentialsAsync(Credentials, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ReturnsFalse_WhenRejected()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Endpoint}/")
            .Respond(HttpStatusCode.Forbidden, "application/xml",
                """<?xml version="1.0"?><Error><Code>AccessDenied</Code><Message>Denied</Message></Error>""");

        var provider = CreateProvider(handler);

        Assert.False(await provider.ValidateCredentialsAsync(Credentials, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ReturnsFalse_ForUnparseableCredentials()
    {
        var provider = CreateProvider(new MockHttpMessageHandler());

        var result = await provider.ValidateCredentialsAsync(
            new ProviderCredentials("not-a-storage-payload"), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ListBucketsAsync_MapsNamesAndSortsThem()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Endpoint}/")
            .Respond(HttpStatusCode.OK, "application/xml", ListBucketsXml);

        var provider = CreateProvider(handler);
        var buckets = await provider.ListBucketsAsync(Credentials, CancellationToken.None);

        Assert.Equal(2, buckets.Count);
        Assert.Equal("another-bucket", buckets[0].Name);
        Assert.Equal(Bucket, buckets[1].Name);
    }

    [Fact]
    public async Task ListObjectsAsync_MapsKeysSizesAndPaging()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Endpoint}/{Bucket}*")
            .Respond(HttpStatusCode.OK, "application/xml", ListObjectsXml);

        var provider = CreateProvider(handler);
        var page = await provider.ListObjectsAsync(
            Credentials, Bucket, prefix: null, continuationToken: null, maxKeys: 100, CancellationToken.None);

        Assert.Equal(2, page.Objects.Count);
        Assert.Equal("photo-one.webp", page.Objects[0].Key);
        Assert.Equal(167230, page.Objects[0].Size);
        Assert.True(page.IsTruncated);
        Assert.Equal("next-page-token", page.NextContinuationToken);
    }

    [Fact]
    public async Task DeleteObjectsAsync_ReturnsDeletedCountAndErrors()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Endpoint}/{Bucket}*")
            .Respond(HttpStatusCode.OK, "application/xml", DeleteResultXml);

        var provider = CreateProvider(handler);
        var result = await provider.DeleteObjectsAsync(
            Credentials, Bucket, ["gone.webp", "locked.webp"], CancellationToken.None);

        Assert.Equal(1, result.DeletedCount);
        var error = Assert.Single(result.Errors);
        Assert.Equal("locked.webp", error.Key);
        Assert.Equal("AccessDenied", error.Code);
    }

    [Fact]
    public async Task DeleteObjectsAsync_ShortCircuits_ForEmptyBatch()
    {
        // No handler configured: proves it never reaches the network.
        var provider = CreateProvider(new MockHttpMessageHandler());

        var result = await provider.DeleteObjectsAsync(Credentials, Bucket, [], CancellationToken.None);

        Assert.Equal(0, result.DeletedCount);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task DeleteObjectsAsync_Rejects_BatchesOverTheS3Limit()
    {
        var provider = CreateProvider(new MockHttpMessageHandler());
        var tooMany = Enumerable.Range(0, ObjectStorageLimits.MaxDeleteBatchSize + 1)
            .Select(i => $"key-{i}")
            .ToList();

        var ex = await Assert.ThrowsAsync<DeployAIException>(() =>
            provider.DeleteObjectsAsync(Credentials, Bucket, tooMany, CancellationToken.None));

        Assert.Equal("storage_delete_batch_too_large", ex.ErrorCode);
    }

    [Fact]
    public async Task GetDownloadUrlAsync_ReturnsPresignedUrlForTheKey()
    {
        var provider = CreateProvider(new MockHttpMessageHandler());

        var url = await provider.GetDownloadUrlAsync(
            Credentials, Bucket, "photo-one.webp", TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.StartsWith(Endpoint, url);
        Assert.Contains(Bucket, url);
        Assert.Contains("photo-one.webp", url);
        Assert.Contains("X-Amz-Signature", url);
    }

    [Fact]
    public async Task Operations_Throw_ForUnparseableCredentials()
    {
        var provider = CreateProvider(new MockHttpMessageHandler());

        var ex = await Assert.ThrowsAsync<DeployAIException>(() =>
            provider.ListBucketsAsync(new ProviderCredentials("{}"), CancellationToken.None));

        Assert.Equal("storage_credentials_invalid", ex.ErrorCode);
    }

    private const string ListBucketsXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <ListAllMyBucketsResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
      <Owner><ID>owner</ID><DisplayName>owner</DisplayName></Owner>
      <Buckets>
        <Bucket><Name>yemeni-breeze-media</Name><CreationDate>2026-07-01T10:00:00.000Z</CreationDate></Bucket>
        <Bucket><Name>another-bucket</Name><CreationDate>2026-07-02T10:00:00.000Z</CreationDate></Bucket>
      </Buckets>
    </ListAllMyBucketsResult>
    """;

    private const string ListObjectsXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
      <Name>yemeni-breeze-media</Name>
      <KeyCount>2</KeyCount>
      <MaxKeys>100</MaxKeys>
      <IsTruncated>true</IsTruncated>
      <NextContinuationToken>next-page-token</NextContinuationToken>
      <Contents>
        <Key>photo-one.webp</Key>
        <LastModified>2026-07-20T12:00:00.000Z</LastModified>
        <Size>167230</Size>
        <StorageClass>STANDARD</StorageClass>
      </Contents>
      <Contents>
        <Key>photo-one-original</Key>
        <LastModified>2026-07-20T12:00:01.000Z</LastModified>
        <Size>8094515</Size>
        <StorageClass>STANDARD</StorageClass>
      </Contents>
    </ListBucketResult>
    """;

    private const string DeleteResultXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <DeleteResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
      <Deleted><Key>gone.webp</Key></Deleted>
      <Error><Key>locked.webp</Key><Code>AccessDenied</Code><Message>Access Denied</Message></Error>
    </DeleteResult>
    """;

    /// <summary>Routes the AWS SDK's transport through MockHttp.</summary>
    private sealed class MockS3HttpClientFactory : Amazon.Runtime.HttpClientFactory
    {
        private readonly MockHttpMessageHandler _handler;

        public MockS3HttpClientFactory(MockHttpMessageHandler handler) => _handler = handler;

        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => _handler.ToHttpClient();
    }
}
