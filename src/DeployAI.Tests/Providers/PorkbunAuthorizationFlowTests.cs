using System.Net;
using System.Text.Json;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Providers.Porkbun;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

/// <summary>
/// Getting Porkbun credentials by approval rather than by asking the user to create and paste a
/// key pair.
/// </summary>
/// <remarks>
/// The states that carry weight are the ones that are not "approved". A denial and an expiry both
/// end the flow, and reporting either as still-pending leaves the UI spinning on something that is
/// never going to happen.
/// </remarks>
public class PorkbunAuthorizationFlowTests
{
    private const string Api = "https://api.porkbun.com/api/json/v3";
    private const string RequestToken = "e3287f9be6b3ac01920b06bd712d22f7";

    private static PorkbunAuthorizationFlow Flow(MockHttpMessageHandler handler) =>
        new(handler.ToHttpClient());

    private static DnsAuthorizationRequest Pending(TimeSpan? remaining = null) =>
        new(RequestToken, $"https://porkbun.com/account/apiKeyApproval/{RequestToken}", "verifier-abc",
            DateTimeOffset.UtcNow.Add(remaining ?? TimeSpan.FromMinutes(9)));

    private static void RespondPoll(MockHttpMessageHandler handler, HttpStatusCode code, string json) =>
        handler.When(HttpMethod.Post, $"{Api}/apikey/retrieve").Respond(code, "application/json", json);

    [Fact]
    public async Task BeginAsync_ReturnsTheApprovalUrlAndKeepsTheVerifier()
    {
        var handler = new MockHttpMessageHandler();
        string? sent = null;
        handler.When(HttpMethod.Post, $"{Api}/apikey/request").Respond(async call =>
        {
            sent = await call.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {"status":"SUCCESS","requestToken":"{{RequestToken}}",
                     "authUrl":"https://porkbun.com/account/apiKeyApproval/{{RequestToken}}",
                     "deliveryMode":"pkce"}
                    """,
                    System.Text.Encoding.UTF8, "application/json")
            };
        });

        var request = await Flow(handler).BeginAsync(CancellationToken.None);

        Assert.Equal(RequestToken, request.RequestToken);
        Assert.Contains("apiKeyApproval", request.ApprovalUrl, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(request.Verifier));
        Assert.True(request.ExpiresAt > DateTimeOffset.UtcNow);

        // PKCE is what makes the poll safe: without a challenge Porkbun returns only the public
        // key, and the secret never arrives.
        var body = JsonDocument.Parse(sent!).RootElement;
        Assert.Equal("S256", body.GetProperty("codeChallengeMethod").GetString());
        var challenge = body.GetProperty("codeChallenge").GetString()!;
        Assert.Equal(43, challenge.Length);
        Assert.DoesNotContain('=', challenge);
        Assert.DoesNotContain('+', challenge);
        Assert.DoesNotContain('/', challenge);
    }

    // The challenge is a hash of the verifier, never the verifier itself — otherwise anyone who
    // saw the request could complete it.
    [Fact]
    public async Task BeginAsync_NeverSendsTheVerifierItself()
    {
        var handler = new MockHttpMessageHandler();
        string? sent = null;
        handler.When(HttpMethod.Post, $"{Api}/apikey/request").Respond(async call =>
        {
            sent = await call.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"status":"SUCCESS","requestToken":"{{RequestToken}}","authUrl":"https://x/y"}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });

        var request = await Flow(handler).BeginAsync(CancellationToken.None);

        Assert.DoesNotContain(request.Verifier, sent!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollAsync_ReportsPendingWhileWaiting()
    {
        var handler = new MockHttpMessageHandler();
        RespondPoll(handler, HttpStatusCode.OK,
            """{"status":"PENDING","message":"Awaiting user authorization."}""");

        var result = await Flow(handler).PollAsync(Pending(), CancellationToken.None);

        Assert.Equal(DnsAuthorizationState.Pending, result.State);
        Assert.Null(result.Credentials);
    }

    [Fact]
    public async Task PollAsync_ReturnsBothKeysOnApproval()
    {
        var handler = new MockHttpMessageHandler();
        RespondPoll(handler, HttpStatusCode.OK,
            """{"status":"SUCCESS","apikey":"pk1_live_abc","secretapikey":"sk1_live_def"}""");

        var result = await Flow(handler).PollAsync(Pending(), CancellationToken.None);

        Assert.Equal(DnsAuthorizationState.Approved, result.State);
        var keys = PorkbunCredentialStorage.TryParse(result.Credentials!.Token);
        Assert.NotNull(keys);
        Assert.Equal("pk1_live_abc", keys!.ApiKey);
        Assert.Equal("sk1_live_def", keys.SecretApiKey);
    }

    // A denial is its own status code. Read as pending, the UI would poll something already dead.
    [Fact]
    public async Task PollAsync_ReportsADenialRatherThanWaitingForever()
    {
        var handler = new MockHttpMessageHandler();
        RespondPoll(handler, HttpStatusCode.Forbidden,
            """{"status":"ERROR","message":"Request denied."}""");

        var result = await Flow(handler).PollAsync(Pending(), CancellationToken.None);

        Assert.Equal(DnsAuthorizationState.Denied, result.State);
        Assert.Contains("declined", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Checked before asking, so a closed window reads as expired rather than as a refusal — the
    // user did nothing wrong, they were just slow.
    [Fact]
    public async Task PollAsync_ReportsExpiryWithoutAskingTheProvider()
    {
        // Nothing registered: any HTTP call would throw.
        var result = await Flow(new MockHttpMessageHandler())
            .PollAsync(Pending(TimeSpan.FromMinutes(-1)), CancellationToken.None);

        Assert.Equal(DnsAuthorizationState.Expired, result.State);
        Assert.Contains("expired", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollAsync_ReportsAnAlreadyUsedTokenAsExpired()
    {
        var handler = new MockHttpMessageHandler();
        RespondPoll(handler, HttpStatusCode.BadRequest,
            """{"status":"ERROR","message":"Token already used."}""");

        var result = await Flow(handler).PollAsync(Pending(), CancellationToken.None);

        Assert.Equal(DnsAuthorizationState.Expired, result.State);
    }

    // Not reachable is not the same as not approved: the request may still be perfectly alive.
    [Fact]
    public async Task PollAsync_TreatsAnUnreachableProviderAsStillWaiting()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/apikey/retrieve").Throw(new HttpRequestException("no route"));

        var result = await Flow(handler).PollAsync(Pending(), CancellationToken.None);

        Assert.Equal(DnsAuthorizationState.Unreachable, result.State);
        Assert.Null(result.Credentials);
    }

    // A success envelope missing the secret is not an approval — treating it as one would store a
    // half credential that fails on first use, long after the one chance to read it has gone.
    [Fact]
    public async Task PollAsync_DoesNotClaimApproval_WhenTheSecretIsAbsent()
    {
        var handler = new MockHttpMessageHandler();
        RespondPoll(handler, HttpStatusCode.OK, """{"status":"SUCCESS","apikey":"pk1_live_abc"}""");

        var result = await Flow(handler).PollAsync(Pending(), CancellationToken.None);

        Assert.NotEqual(DnsAuthorizationState.Approved, result.State);
        Assert.Null(result.Credentials);
    }

    [Fact]
    public async Task BeginAsync_SurfacesRateLimitingUnderTheSharedCode()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/apikey/request")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", """{"status":"ERROR"}""");

        var ex = await Assert.ThrowsAsync<DeployAIException>(
            () => Flow(handler).BeginAsync(CancellationToken.None));

        Assert.Equal(DnsErrorCodes.RateLimited, ex.ErrorCode);
    }
}
