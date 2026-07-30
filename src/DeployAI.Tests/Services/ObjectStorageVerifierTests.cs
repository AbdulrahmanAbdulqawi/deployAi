using System.Net;
using DeployAI.Api.Services;
using DeployAI.Core.Providers;
using Moq;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Services;

/// <summary>
/// Four storage failures in one day were each found only when a person attempted an upload: SigV2
/// presigned URLs, an unsigned payload, a bucket allowing no origin, and provisioning that ran only
/// at creation. Every one passed every check DeployAI had, because those checks confirmed
/// configuration rather than behaviour. These assert on behaviour.
/// </summary>
public class ObjectStorageVerifierTests
{
    private const string Endpoint = "https://hel1.your-objectstorage.com";
    private const string Bucket = "yemenconnect";
    private const string Origin = "http://site.example.com";

    [Fact]
    public async Task VerifyAsync_ReportsBothChecksPassing()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Options, $"{Endpoint}/{Bucket}/deployai-preflight-probe")
            .Respond(_ => Preflight(HttpStatusCode.OK, allowOrigin: Origin));

        var verification = await Verify(handler, StorageRoundTrip.Ok);

        Assert.True(verification.Ok);
        Assert.Contains(verification.Findings, f => f.Contains("wrote, read and deleted"));
        Assert.Contains(verification.Findings, f => f.Contains($"allowed from {Origin}"));
    }

    [Fact]
    public async Task VerifyAsync_FailsAndSaysUploadsWillFail_WhenTheBucketRefusesTheOrigin()
    {
        // The exact failure a browser hit: OPTIONS answered 403 with no Access-Control-Allow-Origin,
        // so the upload never started. Listing buckets would have looked perfectly healthy.
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Options, $"{Endpoint}/{Bucket}/deployai-preflight-probe")
            .Respond(_ => Preflight(HttpStatusCode.Forbidden, allowOrigin: null));

        var verification = await Verify(handler, StorageRoundTrip.Ok);

        Assert.False(verification.Ok);
        Assert.Contains(verification.Findings, f =>
            f.Contains("refuses browser uploads") && f.Contains("preflight"));
    }

    [Fact]
    public async Task VerifyAsync_NamesTheStepThatBroke()
    {
        // "Storage is not working" and "storage can write but not read" send you to entirely
        // different places, so the step travels with the failure.
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Options, "*").Respond(_ => Preflight(HttpStatusCode.OK, Origin));

        var verification = await Verify(
            handler,
            new StorageRoundTrip(false, "write", "The request signature we calculated does not match."));

        Assert.False(verification.Ok);
        Assert.Contains(verification.Findings, f =>
            f.Contains("cannot write") && f.Contains("signature"));
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenThePreflightCannotBeReached()
    {
        // An endpoint that does not answer is a finding, not an absence of one.
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Options, "*").Throw(new HttpRequestException("Name or service not known."));

        var verification = await Verify(handler, StorageRoundTrip.Ok);

        Assert.False(verification.Ok);
        Assert.Contains(verification.Findings, f => f.Contains("refuses browser uploads"));
    }

    [Fact]
    public async Task VerifyAsync_StillChecksTheRoundTrip_WhenNoWebsiteOriginIsKnown()
    {
        // No origin means no preflight to run, but the signed path still has to be proven.
        var handler = new MockHttpMessageHandler();

        var verification = await Verify(handler, StorageRoundTrip.Ok, origins: []);

        Assert.True(verification.Ok);
        Assert.Single(verification.Findings);
        Assert.Contains("wrote, read and deleted", verification.Findings[0]);
    }

    [Fact]
    public async Task VerifyAsync_ReportsAnUnusableEndpointRatherThanThrowing()
    {
        var handler = new MockHttpMessageHandler();

        var verification = await Verify(handler, StorageRoundTrip.Ok, endpoint: "not-a-url");

        Assert.False(verification.Ok);
        Assert.Contains(verification.Findings, f => f.Contains("not a valid absolute URL"));
    }

    private static HttpResponseMessage Preflight(HttpStatusCode status, string? allowOrigin)
    {
        var response = new HttpResponseMessage(status);
        if (allowOrigin is not null)
        {
            response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", allowOrigin);
        }

        return response;
    }

    [Fact]
    public async Task VerifyAsync_SaysHowFarTheCredentialsReach_WhenTheAccountHasOtherBuckets()
    {
        // Hetzner issues S3 credentials per project, valid for every bucket in it, and keys can only
        // be made in its console -- so DeployAI cannot narrow this itself. It can refuse to let the
        // blast radius stay invisible.
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Options, "*").Respond(_ => Preflight(HttpStatusCode.OK, Origin));

        var verification = await Verify(handler, StorageRoundTrip.Ok, buckets: 4);

        Assert.True(verification.Ok, "wider reach is a disclosure, not a failure");
        Assert.Contains(verification.Findings, f =>
            f.Contains("all 4 buckets") && f.Contains("own key pair"));
    }

    [Fact]
    public async Task VerifyAsync_StaysSilentAboutReach_WhenTheAppOwnsTheOnlyBucket()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Options, "*").Respond(_ => Preflight(HttpStatusCode.OK, Origin));

        var verification = await Verify(handler, StorageRoundTrip.Ok, buckets: 1);

        Assert.DoesNotContain(verification.Findings, f => f.Contains("buckets in the storage account"));
    }

    private static async Task<ObjectStorageVerification> Verify(
        MockHttpMessageHandler handler,
        StorageRoundTrip roundTrip,
        IReadOnlyList<string>? origins = null,
        string endpoint = Endpoint,
        int buckets = 1)
    {
        var provider = new Mock<IObjectStorageProvider>();
        provider.Setup(p => p.VerifyRoundTripAsync(
                It.IsAny<ProviderCredentials>(), Bucket, It.IsAny<CancellationToken>()))
            .ReturnsAsync(roundTrip);
        provider.Setup(p => p.ListBucketsAsync(It.IsAny<ProviderCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, buckets)
                .Select(i => new StorageBucket($"bucket-{i}", DateTimeOffset.UtcNow))
                .ToList());

        var verifier = new ObjectStorageVerifier(
            new StubHttpClientFactory(handler),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ObjectStorageVerifier>.Instance);

        return await verifier.VerifyAsync(
            provider.Object,
            new ProviderCredentials("token"),
            endpoint,
            Bucket,
            origins ?? [Origin],
            CancellationToken.None);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
