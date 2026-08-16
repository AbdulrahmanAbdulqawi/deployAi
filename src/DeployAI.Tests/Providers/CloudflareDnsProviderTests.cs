using System.Net;
using System.Text.Json;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Providers.Cloudflare;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

/// <summary>
/// Writing records into a user's own zone, and telling them precisely why a token will not do.
/// </summary>
/// <remarks>
/// The payloads below are the ones Cloudflare actually returns, captured against the live API —
/// including the shapes that are absent from its published error schema, such as the
/// <c>error_chain</c> that carries the only code meaning "that is not a token".
/// </remarks>
public class CloudflareDnsProviderTests
{
    private const string Api = "https://api.cloudflare.com/client/v4";
    private static readonly ProviderCredentials Credentials = new("cfut_realish_token_value");

    private static CloudflareDnsProvider Provider(MockHttpMessageHandler handler) =>
        new(handler.ToHttpClient());

    private sealed class JsonCapture
    {
        public JsonElement? Value { get; set; }
    }

    private static JsonCapture CaptureJson(MockedRequest request, string response)
    {
        var capture = new JsonCapture();
        request.Respond(async call =>
        {
            if (call.Content is not null)
            {
                capture.Value = JsonDocument.Parse(await call.Content.ReadAsStringAsync()).RootElement.Clone();
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            };
        });
        return capture;
    }

    private static string ZonePage(string zonesJson, int page = 1, int totalPages = 1) =>
        "{\"success\":true,\"errors\":[],\"messages\":[],\"result\":[" + zonesJson + "]," +
        "\"result_info\":{\"page\":" + page + ",\"per_page\":50,\"count\":1,\"total_count\":1," +
        "\"total_pages\":" + totalPages + "}}";

    private const string ActiveFullZone =
        """{"id":"zone-1","name":"example.com","status":"active","type":"full","account":{"name":"Acme"}}""";

    private static void RespondWithZones(MockHttpMessageHandler handler, string body) =>
        handler.When(HttpMethod.Get, $"{Api}/zones*").Respond(HttpStatusCode.OK, "application/json", body);

    private static void RespondWithNoExistingRecord(MockHttpMessageHandler handler) =>
        handler.When(HttpMethod.Get, $"{Api}/zones/zone-1/dns_records*")
            .Respond(HttpStatusCode.OK, "application/json",
                """{"success":true,"errors":[],"result":[]}""");

    // ---- writing records -------------------------------------------------

    [Fact]
    public async Task UpsertAddressRecordAsync_WritesTheRecordUnproxied()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithNoExistingRecord(handler);
        var body = CaptureJson(
            handler.When(HttpMethod.Post, $"{Api}/zones/zone-1/dns_records"),
            """{"success":true,"result":{"id":"record-1"}}""");

        await Provider(handler).UpsertAddressRecordAsync(
            Credentials, "zone-1", "app.example.com", "46.225.80.188", CancellationToken.None);

        Assert.NotNull(body.Value);
        Assert.False(body.Value!.Value.GetProperty("proxied").GetBoolean());
        Assert.Equal("A", body.Value.Value.GetProperty("type").GetString());
        Assert.Equal("app.example.com", body.Value.Value.GetProperty("name").GetString());
        Assert.Equal("46.225.80.188", body.Value.Value.GetProperty("content").GetString());
    }

    // Duplicate detection keys on name+type+content, so an update written as a create does not
    // error -- it succeeds, and leaves two A records round-robining half the traffic to a dead IP.
    [Fact]
    public async Task UpsertAddressRecordAsync_UpdatesTheExistingRecord_RatherThanAddingASecond()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Api}/zones/zone-1/dns_records*")
            .Respond(HttpStatusCode.OK, "application/json",
                """{"success":true,"result":[{"id":"record-1","name":"app.example.com","content":"1.2.3.4"}]}""");
        handler.When(HttpMethod.Put, $"{Api}/zones/zone-1/dns_records/record-1")
            .Respond(HttpStatusCode.OK, "application/json", """{"success":true,"result":{"id":"record-1"}}""");
        // No POST is registered; MockHttp throws on an unmatched request, so a second record
        // being created is what would fail this test.

        var result = await Provider(handler).UpsertAddressRecordAsync(
            Credentials, "zone-1", "app.example.com", "46.225.80.188", CancellationToken.None);

        Assert.False(result.Created);
        Assert.Equal("record-1", result.RecordId);
    }

    // 60 is the lowest Cloudflare accepts: there is no legal TTL between 2 and 59, 30 is
    // Enterprise-only, and 1 means "automatic" (300s), which is too slow for a setup poll.
    [Fact]
    public async Task UpsertAddressRecordAsync_UsesTheShortestLegalTtl()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithNoExistingRecord(handler);
        var body = CaptureJson(
            handler.When(HttpMethod.Post, $"{Api}/zones/zone-1/dns_records"),
            """{"success":true,"result":{"id":"record-1"}}""");

        await Provider(handler).UpsertAddressRecordAsync(
            Credentials, "zone-1", "app.example.com", "46.225.80.188", CancellationToken.None);

        Assert.Equal(60, body.Value!.Value.GetProperty("ttl").GetInt32());
    }

    [Fact]
    public async Task UpsertAddressRecordAsync_MarksTheRecordAsManaged()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithNoExistingRecord(handler);
        var body = CaptureJson(
            handler.When(HttpMethod.Post, $"{Api}/zones/zone-1/dns_records"),
            """{"success":true,"result":{"id":"record-1"}}""");

        await Provider(handler).UpsertAddressRecordAsync(
            Credentials, "zone-1", "app.example.com", "46.225.80.188", CancellationToken.None);

        Assert.Contains("DeployAI", body.Value!.Value.GetProperty("comment").GetString()!);
    }

    [Fact]
    public async Task DeleteRecordAsync_TreatsAMissingRecordAsRemoved()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Delete, $"{Api}/zones/zone-1/dns_records/record-1")
            .Respond(HttpStatusCode.NotFound, "application/json", """{"success":false}""");

        Assert.True(await Provider(handler).DeleteRecordAsync(
            Credentials, "zone-1", "record-1", CancellationToken.None));
    }

    // ---- listing zones ---------------------------------------------------

    // 50 is Cloudflare's maximum page size, so a single request silently loses everything after
    // the fiftieth zone -- and the domain whose zone was cut off falls back to asking the user to
    // add a record by hand, with nothing explaining why.
    [Fact]
    public async Task ListZonesAsync_FollowsEveryPage()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Api}/zones").WithQueryString("per_page=50&page=1")
            .Respond(HttpStatusCode.OK, "application/json", ZonePage(ActiveFullZone, 1, 2));
        handler.When(HttpMethod.Get, $"{Api}/zones").WithQueryString("per_page=50&page=2")
            .Respond(HttpStatusCode.OK, "application/json", ZonePage(
                """{"id":"zone-2","name":"second.com","status":"active","type":"full"}""", 2, 2));

        var zones = await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None);

        Assert.Equal(["example.com", "second.com"], zones.Select(z => z.Name));
    }

    [Fact]
    public async Task ListZonesAsync_StopsAtTheLastPage()
    {
        var handler = new MockHttpMessageHandler();
        // Only page 1 is registered; asking for page 2 would throw on an unmatched request.
        handler.When(HttpMethod.Get, $"{Api}/zones").WithQueryString("per_page=50&page=1")
            .Respond(HttpStatusCode.OK, "application/json", ZonePage(ActiveFullZone));

        var zones = await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None);

        Assert.Single(zones);
    }

    [Fact]
    public async Task ListZonesAsync_ReportsAnActiveZoneAsReady()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler, ZonePage(ActiveFullZone));

        var zone = Assert.Single(await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsZoneUsability.Ready, zone.Usability);
        Assert.True(zone.IsReady);
        Assert.Equal("Acme", zone.AccountName);
    }

    // Records can be written into a pending zone perfectly happily, and resolve to nothing --
    // so the domain waits out its deadline and is reported as the user's mistake.
    [Fact]
    public async Task ListZonesAsync_ReportsAPendingZoneAsNotDelegated()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler, ZonePage(
            """{"id":"z","name":"pending.com","status":"pending","type":"full"}"""));

        var zone = Assert.Single(await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsZoneUsability.NotDelegated, zone.Usability);
        Assert.False(zone.IsReady);
        Assert.Contains("nameservers", zone.UsabilityMessage, StringComparison.OrdinalIgnoreCase);
    }

    // The failure that looks healthiest: a partial or secondary zone reports status "active"
    // while Cloudflare is not the authority for the names in it.
    [Theory]
    [InlineData("partial")]
    [InlineData("secondary")]
    public async Task ListZonesAsync_ReportsAnActiveButNonAuthoritativeZone(string type)
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler, ZonePage(
            "{\"id\":\"z\",\"name\":\"partial.com\",\"status\":\"active\",\"type\":\"" + type + "\"}"));

        var zone = Assert.Single(await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsZoneUsability.NotAuthoritative, zone.Usability);
        Assert.False(zone.IsReady);
    }

    [Fact]
    public async Task ListZonesAsync_ReportsAnUnrecognisedStatusWithoutFailing()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler, ZonePage(
            """{"id":"z","name":"odd.com","status":"moved","type":"full"}"""));

        var zone = Assert.Single(await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsZoneUsability.Unknown, zone.Usability);
        Assert.Contains("moved", zone.UsabilityMessage, StringComparison.OrdinalIgnoreCase);
    }

    // A zone whose only oddity is being paused is still fine for DNS: pausing turns off proxying,
    // not resolution, and flagging it would false-alarm on exactly the DNS-only zones we want.
    [Fact]
    public async Task ListZonesAsync_IgnoresPaused()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler, ZonePage(
            """{"id":"z","name":"paused.com","status":"active","type":"full","paused":true}"""));

        Assert.True(Assert.Single(
            await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None)).IsReady);
    }

    // Every zone carries an explanation, or the UI has nothing to show for the ones it disables.
    [Fact]
    public async Task ListZonesAsync_AlwaysExplainsEachZone()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler, ZonePage(
            ActiveFullZone + ",{\"id\":\"z2\",\"name\":\"p.com\",\"status\":\"pending\",\"type\":\"full\"}"));

        var zones = await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None);

        Assert.All(zones, z => Assert.False(string.IsNullOrWhiteSpace(z.UsabilityMessage)));
    }

    // ---- validating a token ----------------------------------------------

    [Fact]
    public async Task ValidateCredentialsAsync_AcceptsATokenThatCanListZones()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler, ZonePage(ActiveFullZone));
        handler.When(HttpMethod.Get, $"{Api}/user/tokens/verify").Respond(
            HttpStatusCode.OK, "application/json",
            """{"success":true,"result":{"id":"t","status":"active","expires_on":"2027-01-31T00:00:00Z"}}""");

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Ok, check.Verdict);
        Assert.True(check.IsUsable);
        Assert.Single(check.Zones);
        Assert.Equal(2027, check.TokenExpiresOn!.Value.Year);
    }

    // An account-owned token — the kind Cloudflare recommends for service integrations — verifies
    // at a different path and returns a flat 401 here despite being entirely valid. Letting that
    // count would reject the better sort of token.
    [Fact]
    public async Task ValidateCredentialsAsync_StillAcceptsATokenWhoseExpiryCannotBeRead()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler, ZonePage(ActiveFullZone));
        handler.When(HttpMethod.Get, $"{Api}/user/tokens/verify").Respond(
            HttpStatusCode.Unauthorized, "application/json",
            """{"success":false,"errors":[{"code":1000,"message":"Invalid API Token"}],"result":null}""");

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Ok, check.Verdict);
        Assert.Null(check.TokenExpiresOn);
    }

    // 400/6003 with 6111 in the chain is what a truncated copy-paste produces.
    [Fact]
    public async Task ValidateCredentialsAsync_ReportsAMalformedToken()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Api}/zones*").Respond(
            HttpStatusCode.BadRequest, "application/json",
            """{"success":false,"errors":[{"code":6003,"message":"Invalid request headers","error_chain":[{"code":6111,"message":"Invalid format for Authorization header"}]}],"messages":[],"result":null}""");

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Malformed, check.Verdict);
        Assert.Contains("cut short", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ReportsARejectedToken()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Api}/zones*").Respond(
            HttpStatusCode.Unauthorized, "application/json",
            """{"success":false,"errors":[{"code":1000,"message":"Invalid API Token"}],"result":null}""");

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Rejected, check.Verdict);
        Assert.True(check.IsConclusive);
    }

    // The likeliest first attempt of all: Cloudflare's own "Edit zone DNS" template grants
    // DNS Write and no Zone Read, so the token can change records but cannot see the domains.
    [Fact]
    public async Task ValidateCredentialsAsync_ExplainsATokenThatCannotListZones()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Api}/zones*").Respond(
            HttpStatusCode.Forbidden, "application/json",
            """{"success":false,"errors":[{"code":0,"message":"Actor 'x' requires permission 'com.cloudflare.api.account.zone.list' to list zones"}],"result":null}""");

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.CannotListZones, check.Verdict);
        Assert.Contains("Zone → Zone → Read", check.Message, StringComparison.Ordinal);
        Assert.Contains("DNS → Edit", check.Message, StringComparison.Ordinal);
    }

    // Under-scoping returns a perfectly successful envelope with an empty list rather than a 403,
    // so an empty result has to be its own outcome and not a pass.
    // An empty zone list has three causes and they are not equally likely. The first real user to
    // hit this had no domains in Cloudflare at all — the message walked them through permissions
    // and Zone Resources, neither of which could ever have helped, because there was nothing to
    // list. "Have you added a domain yet" has to come first: it is the only one of the three that
    // no change to the token can fix.
    [Fact]
    public async Task ValidateCredentialsAsync_AsksWhetherAnyDomainExists_BeforeBlamingTheToken()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler,
            """{"success":true,"errors":[],"result":[],"result_info":{"page":1,"total_pages":1}}""");
        handler.When(HttpMethod.Get, $"{Api}/accounts*")
            .Respond(HttpStatusCode.Forbidden, "application/json", """{"success":false,"errors":[]}""");

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.NoZonesVisible, check.Verdict);
        Assert.False(check.IsUsable);

        // Named, and named before the two token-shaped explanations.
        var emptyList = check.Message.IndexOf("Websites list is", StringComparison.Ordinal);
        var scope = check.Message.IndexOf("Zone Resources", StringComparison.Ordinal);
        Assert.True(emptyList >= 0, "the no-domains case must be mentioned");
        Assert.True(scope > emptyList, "the no-domains case must come before the scope advice");
    }

    // "Check you used the right account" is unanswerable from the screen it appears on. Naming the
    // account turns it into something confirmable at a glance.
    [Fact]
    public async Task ValidateCredentialsAsync_NamesTheAccount_WhenItCanBeDiscovered()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler,
            """{"success":true,"errors":[],"result":[],"result_info":{"page":1,"total_pages":1}}""");
        handler.When(HttpMethod.Get, $"{Api}/accounts*").Respond(
            HttpStatusCode.OK, "application/json",
            """{"success":true,"errors":[],"result":[{"id":"a1","name":"Personal"}]}""");

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Contains("Personal", check.Message, StringComparison.Ordinal);
    }

    // A zone-scoped token cannot list accounts at all, so that lookup failing is expected and must
    // not turn a clear explanation into an error of its own.
    [Fact]
    public async Task ValidateCredentialsAsync_StillExplainsItself_WhenTheAccountCannotBeRead()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithZones(handler,
            """{"success":true,"errors":[],"result":[],"result_info":{"page":1,"total_pages":1}}""");
        handler.When(HttpMethod.Get, $"{Api}/accounts*").Throw(new HttpRequestException("nope"));

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.NoZonesVisible, check.Verdict);
        Assert.Contains("Zone Resources", check.Message, StringComparison.Ordinal);
        Assert.Contains("more than one Cloudflare account", check.Message, StringComparison.Ordinal);
    }

    // Neither of these says anything about the token, so neither may be treated as a verdict on it.
    [Fact]
    public async Task ValidateCredentialsAsync_TreatsRateLimitingAsInconclusive()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Api}/zones*").Respond(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"success":false,"errors":[]}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.Add("Retry-After", "42");
            return response;
        });

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.RateLimited, check.Verdict);
        Assert.False(check.IsConclusive);
        Assert.Equal(42, (int)check.RetryAfter!.Value.TotalSeconds);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_TreatsAnUnreachableProviderAsInconclusive()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Api}/zones*").Throw(new HttpRequestException("no route"));

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Unreachable, check.Verdict);
        Assert.False(check.IsConclusive);
    }

    // Refused offline: the global key authenticates with an email/key header pair, never a bearer
    // token, so sending it would produce a baffling 403 rather than a useful sentence.
    [Theory]
    [InlineData("cfk_thisisaglobalapikey", "Global API Key")]
    [InlineData("v1.0-abcdef", "Origin CA key")]
    public async Task ValidateCredentialsAsync_RefusesTheWrongKindOfKey_WithoutCallingCloudflare(
        string token, string expected)
    {
        // Nothing is registered on the handler: any HTTP call would throw.
        var check = await Provider(new MockHttpMessageHandler())
            .ValidateCredentialsAsync(new ProviderCredentials(token), CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Malformed, check.Verdict);
        Assert.Contains(expected, check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_RefusesAnEmptyToken()
    {
        var check = await Provider(new MockHttpMessageHandler())
            .ValidateCredentialsAsync(new ProviderCredentials("  "), CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Malformed, check.Verdict);
    }

    // ---- error codes reaching the API edge --------------------------------

    [Fact]
    public async Task OperationalCalls_SurfaceRateLimitingUnderItsOwnErrorCode()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Api}/zones*")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", """{"success":false,"errors":[]}""");

        var ex = await Assert.ThrowsAsync<DeployAIException>(
            () => Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsErrorCodes.RateLimited, ex.ErrorCode);
    }

    [Fact]
    public async Task OperationalCalls_SurfaceAnUnreachableProviderUnderItsOwnErrorCode()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{Api}/zones*").Throw(new HttpRequestException("no route"));

        var ex = await Assert.ThrowsAsync<DeployAIException>(
            () => Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsErrorCodes.Unreachable, ex.ErrorCode);
    }

    [Fact]
    public async Task AnyCall_RefusesAConnectionWithNoToken()
    {
        var ex = await Assert.ThrowsAsync<DeployAIException>(
            () => Provider(new MockHttpMessageHandler())
                .ListZonesAsync(new ProviderCredentials("  "), CancellationToken.None));

        Assert.Equal("cloudflare_credentials_invalid", ex.ErrorCode);
    }
}
