using System.Net;
using System.Text.Json;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Providers.Porkbun;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

/// <summary>
/// Porkbun's DNS behaviour, against the payloads its API actually returns.
/// </summary>
/// <remarks>
/// Two of these guard mistakes that are invisible rather than loud. Porkbun names a record by its
/// label where Cloudflare names it by the full hostname, so getting it backwards creates
/// <c>app.example.com.example.com</c> and reports success. And API access is off per domain by
/// default, so a perfectly good key silently cannot touch a domain until that is switched on.
/// </remarks>
public class PorkbunDnsProviderTests
{
    private const string Api = "https://api.porkbun.com/api/json/v3";

    private static readonly ProviderCredentials Credentials =
        new(PorkbunCredentialStorage.Serialize("pk1_live_key", "sk1_live_secret"));

    private static PorkbunDnsProvider Provider(MockHttpMessageHandler handler) =>
        new(handler.ToHttpClient());

    private sealed class JsonCapture
    {
        public JsonElement? Value { get; set; }
    }

    private static JsonCapture Capture(MockedRequest request, string response)
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

    private static void RespondOk(MockHttpMessageHandler handler, string path, string json) =>
        handler.When(HttpMethod.Post, $"{Api}/{path}").Respond(HttpStatusCode.OK, "application/json", json);

    private static void RespondPingOk(MockHttpMessageHandler handler, bool sandbox = false) =>
        RespondOk(handler, "ping",
            $$"""{"status":"SUCCESS","credentialsValid":true,"sandbox":{{(sandbox ? "true" : "false")}}}""");

    private static string DomainList(params string[] entries) =>
        "{\"status\":\"SUCCESS\",\"domains\":[" + string.Join(",", entries) + "]}";

    /// <summary>
    /// <paramref name="apiAccess"/> is written unquoted here, but the live API returns it quoted —
    /// see the string-form test below, which is the shape actually observed.
    /// </summary>
    private static string Domain(string name, string status = "ACTIVE", int apiAccess = 1) =>
        $"{{\"domain\":\"{name}\",\"status\":\"{status}\",\"apiAccess\":{apiAccess}}}";

    // ---- the label-not-FQDN trap ----------------------------------------

    [Theory]
    [InlineData("example.com", "app.example.com", "app")]
    [InlineData("example.com", "example.com", "")]
    [InlineData("example.com", "a.b.example.com", "a.b")]
    [InlineData("example.com", "APP.EXAMPLE.COM", "APP")]
    public void ToSubdomain_StripsTheZone(string zone, string hostname, string expected)
    {
        Assert.Equal(expected, PorkbunDnsProvider.ToSubdomain(zone, hostname));
    }

    // Cloudflare's equivalent test asserts the opposite -- that the body carries the full
    // hostname. Sending that here appends the zone again, and Porkbun accepts it happily.
    [Fact]
    public async Task UpsertAddressRecordAsync_SendsTheLabelOnly_NotTheFullHostname()
    {
        var handler = new MockHttpMessageHandler();
        RespondOk(handler, "dns/retrieveByNameType/example.com/A/app", """{"status":"SUCCESS","records":[]}""");
        var body = Capture(
            handler.When(HttpMethod.Post, $"{Api}/dns/create/example.com"),
            """{"status":"SUCCESS","id":"rec-1"}""");

        await Provider(handler).UpsertAddressRecordAsync(
            Credentials, "example.com", "app.example.com", "46.225.80.188", CancellationToken.None);

        Assert.Equal("app", body.Value!.Value.GetProperty("name").GetString());
        Assert.Equal("A", body.Value.Value.GetProperty("type").GetString());
        Assert.Equal("46.225.80.188", body.Value.Value.GetProperty("content").GetString());
    }

    // Porkbun contradicts itself within one resource: create returns the record id as a number,
    // retrieve returns the same id as a string. Both observed live. A strict reader turns a record
    // that was created perfectly well into a reported failure.
    [Theory]
    [InlineData("534057924")]
    [InlineData("\"534057924\"")]
    public async Task UpsertAddressRecordAsync_ReadsTheNewRecordId_WhetherNumberOrString(string idJson)
    {
        var handler = new MockHttpMessageHandler();
        RespondOk(handler, "dns/retrieveByNameType/example.com/A/app", """{"status":"SUCCESS","records":[]}""");
        RespondOk(handler, "dns/create/example.com",
            "{\"status\":\"SUCCESS\",\"id\":" + idJson + "}");

        var result = await Provider(handler).UpsertAddressRecordAsync(
            Credentials, "example.com", "app.example.com", "46.225.80.188", CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal("534057924", result.RecordId);
    }

    [Fact]
    public async Task UpsertAddressRecordAsync_EditsAnExistingRecord_RatherThanAddingASecond()
    {
        var handler = new MockHttpMessageHandler();
        RespondOk(handler, "dns/retrieveByNameType/example.com/A/app",
            """{"status":"SUCCESS","records":[{"id":"rec-1","content":"1.2.3.4"}]}""");
        RespondOk(handler, "dns/editByNameType/example.com/A/app", """{"status":"SUCCESS"}""");
        // No create is registered; MockHttp throws on an unmatched request, so a second record
        // being added is what would fail this test.

        var result = await Provider(handler).UpsertAddressRecordAsync(
            Credentials, "example.com", "app.example.com", "46.225.80.188", CancellationToken.None);

        Assert.False(result.Created);
        Assert.Equal("rec-1", result.RecordId);
    }

    [Fact]
    public async Task EveryRequest_CarriesBothKeysInTheBody()
    {
        var handler = new MockHttpMessageHandler();
        var body = Capture(handler.When(HttpMethod.Post, $"{Api}/ping"),
            """{"status":"SUCCESS","credentialsValid":true,"sandbox":false}""");
        RespondOk(handler, "domain/listAll", DomainList(Domain("example.com")));

        await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal("pk1_live_key", body.Value!.Value.GetProperty("apikey").GetString());
        Assert.Equal("sk1_live_secret", body.Value.Value.GetProperty("secretapikey").GetString());
    }

    // ---- zone usability --------------------------------------------------

    [Fact]
    public async Task ListZonesAsync_ReportsAnActiveOptedInDomainAsReady()
    {
        var handler = new MockHttpMessageHandler();
        RespondOk(handler, "domain/listAll", DomainList(Domain("example.com")));

        var zone = Assert.Single(await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsZoneUsability.Ready, zone.Usability);
        Assert.True(zone.IsReady);
        // Porkbun has no separate zone id; the domain name is the identifier.
        Assert.Equal("example.com", zone.Id);
    }

    // The trap: a key with every permission still cannot touch a domain that has not been opted
    // in, and the flag is right there in the listing, so it is answerable rather than mysterious.
    [Fact]
    public async Task ListZonesAsync_NamesTheApiAccessToggle_WhenADomainIsNotOptedIn()
    {
        var handler = new MockHttpMessageHandler();
        RespondOk(handler, "domain/listAll", DomainList(Domain("example.com", apiAccess: 0)));

        var zone = Assert.Single(await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsZoneUsability.ReadOnly, zone.Usability);
        Assert.False(zone.IsReady);
        Assert.Contains("API Access", zone.UsabilityMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Domain Management", zone.UsabilityMessage, StringComparison.OrdinalIgnoreCase);
    }

    // What the live API actually sends. A strict reader rejects a string where an int is declared,
    // and the failure surfaced as an unhandled parse error rather than anything actionable.
    [Theory]
    [InlineData("\"1\"", DnsZoneUsability.Ready)]
    [InlineData("1", DnsZoneUsability.Ready)]
    [InlineData("\"0\"", DnsZoneUsability.ReadOnly)]
    [InlineData("0", DnsZoneUsability.ReadOnly)]
    public async Task ListZonesAsync_ReadsApiAccess_WhetherQuotedOrNot(
        string apiAccessJson, DnsZoneUsability expected)
    {
        var handler = new MockHttpMessageHandler();
        RespondOk(handler, "domain/listAll",
            "{\"status\":\"SUCCESS\",\"domains\":[{\"domain\":\"example.com\"," +
            "\"status\":\"ACTIVE\",\"apiAccess\":" + apiAccessJson + "}]}");

        var zone = Assert.Single(await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(expected, zone.Usability);
    }

    [Fact]
    public async Task ListZonesAsync_ReportsAnUnexpectedStatusWithoutFailing()
    {
        var handler = new MockHttpMessageHandler();
        RespondOk(handler, "domain/listAll", DomainList(Domain("pending.com", status: "PENDING")));

        var zone = Assert.Single(await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsZoneUsability.Unknown, zone.Usability);
        Assert.Contains("PENDING", zone.UsabilityMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListZonesAsync_AlwaysExplainsEachZone()
    {
        var handler = new MockHttpMessageHandler();
        RespondOk(handler, "domain/listAll",
            DomainList(Domain("a.com"), Domain("b.com", apiAccess: 0), Domain("c.com", status: "X")));

        var zones = await Provider(handler).ListZonesAsync(Credentials, CancellationToken.None);

        Assert.Equal(3, zones.Count);
        Assert.All(zones, z => Assert.False(string.IsNullOrWhiteSpace(z.UsabilityMessage)));
    }

    // ---- validation ------------------------------------------------------

    [Fact]
    public async Task ValidateCredentialsAsync_AcceptsAWorkingKeyPair()
    {
        var handler = new MockHttpMessageHandler();
        RespondPingOk(handler);
        RespondOk(handler, "domain/listAll", DomainList(Domain("example.com")));

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Ok, check.Verdict);
        Assert.Single(check.Zones);
    }

    // A sandbox key spends no real money, and the only way to tell is its prefix — so it is read
    // rather than asked about, and said out loud so the two can never be confused.
    [Fact]
    public async Task ValidateCredentialsAsync_SaysWhenTheKeysAreForTheSandbox()
    {
        var handler = new MockHttpMessageHandler();
        RespondPingOk(handler, sandbox: true);
        RespondOk(handler, "domain/listAll", DomainList(Domain("example.com")));
        var sandboxKeys = new ProviderCredentials(
            PorkbunCredentialStorage.Serialize("pk1_sb_abc", "sk1_sb_def"));

        var check = await Provider(handler).ValidateCredentialsAsync(sandboxKeys, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Ok, check.Verdict);
        Assert.Contains("test mode", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_RejectsKeysPorkbunDoesNotAccept()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/ping").Respond(
            HttpStatusCode.OK, "application/json",
            """{"status":"ERROR","message":"Invalid API key. (002)"}""");

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Rejected, check.Verdict);
        Assert.True(check.IsConclusive);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_RefusesWhenEitherKeyIsMissing()
    {
        var check = await Provider(new MockHttpMessageHandler())
            .ValidateCredentialsAsync(new ProviderCredentials("not-a-packed-pair"), CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Malformed, check.Verdict);
        Assert.Contains("secret API key", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    // An account with no domains cannot be fixed by any key, so it is said plainly rather than
    // dressed up as a credential problem.
    [Fact]
    public async Task ValidateCredentialsAsync_SaysSoWhenTheAccountHoldsNoDomains()
    {
        var handler = new MockHttpMessageHandler();
        RespondPingOk(handler);
        RespondOk(handler, "domain/listAll", DomainList());

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.NoZonesVisible, check.Verdict);
        Assert.Contains("no domains in this Porkbun account", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The same empty listing means two different things, and only one of them is worth spending
    // money on. Sandbox keys address a separate, always-empty account, so "buy or transfer a
    // domain first" is advice to buy a real domain that the test account will still never show.
    [Fact]
    public async Task ValidateCredentialsAsync_SaysTheAccountIsTheSandboxOne_WhenSandboxKeysSeeNoDomains()
    {
        var handler = new MockHttpMessageHandler();
        RespondPingOk(handler, sandbox: true);
        RespondOk(handler, "domain/listAll", DomainList());
        var sandboxKeys = new ProviderCredentials(
            PorkbunCredentialStorage.Serialize("pk1_sb_abc", "sk1_sb_def"));

        var check = await Provider(handler).ValidateCredentialsAsync(sandboxKeys, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.NoZonesVisible, check.Verdict);
        Assert.Contains("test mode", check.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Buy or transfer", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    // An empty account is not a broken key pair, and /ping has already said so. Without this the
    // connection is refused, and buying the account's first domain becomes unreachable.
    [Fact]
    public async Task ValidateCredentialsAsync_ProvesTheKeys_WhenTheAccountIsMerelyEmpty()
    {
        var handler = new MockHttpMessageHandler();
        RespondPingOk(handler);
        RespondOk(handler, "domain/listAll", DomainList());

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.NoZonesVisible, check.Verdict);
        Assert.True(check.CredentialsProven);
        Assert.True(check.IsStorable);
        // Still not usable for DNS — there is no zone to write into yet.
        Assert.False(check.IsUsable);
    }

    // Keys Porkbun refuses are proven wrong, not proven right.
    [Fact]
    public async Task ValidateCredentialsAsync_ProvesNothing_WhenTheKeysAreRejected()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/ping").Respond(
            HttpStatusCode.OK, "application/json",
            """{"status":"ERROR","message":"Invalid API key. (002)"}""");

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.False(check.CredentialsProven);
        Assert.False(check.IsStorable);
    }

    // Porkbun answers this itself on /ping, which beats reading the prefix: a key that does not
    // follow the naming convention would otherwise be reported as a live account by mistake.
    [Fact]
    public async Task ValidateCredentialsAsync_BelievesPorkbunOverThePrefix_WhenPingSaysSandbox()
    {
        var handler = new MockHttpMessageHandler();
        RespondPingOk(handler, sandbox: true);
        RespondOk(handler, "domain/listAll", DomainList(Domain("example.com")));

        // Keys with no sandbox prefix at all — only /ping knows.
        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Ok, check.Verdict);
        Assert.Contains("test mode", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Neither of these says anything about the keys.
    [Fact]
    public async Task ValidateCredentialsAsync_TreatsRateLimitingAsInconclusive()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/ping")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", """{"status":"ERROR"}""");

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.RateLimited, check.Verdict);
        Assert.False(check.IsConclusive);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_TreatsAnUnreachableProviderAsInconclusive()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/ping").Throw(new HttpRequestException("no route"));

        var check = await Provider(handler).ValidateCredentialsAsync(Credentials, CancellationToken.None);

        Assert.Equal(DnsCredentialVerdict.Unreachable, check.Verdict);
        Assert.False(check.IsConclusive);
    }

    // Shared codes, so the API edge answers 429 and 503 for Porkbun exactly as it does for
    // Cloudflare rather than defaulting to 400.
    [Fact]
    public async Task OperationalCalls_UseTheSharedRateLimitCode()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/listAll")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", """{"status":"ERROR"}""");

        var ex = await Assert.ThrowsAsync<DeployAIException>(
            () => Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsErrorCodes.RateLimited, ex.ErrorCode);
    }

    [Fact]
    public async Task OperationalCalls_UseTheSharedUnreachableCode()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/listAll").Throw(new HttpRequestException("no route"));

        var ex = await Assert.ThrowsAsync<DeployAIException>(
            () => Provider(handler).ListZonesAsync(Credentials, CancellationToken.None));

        Assert.Equal(DnsErrorCodes.Unreachable, ex.ErrorCode);
    }

    // ---- credential packing ---------------------------------------------

    [Fact]
    public void PackCredential_RoundTripsBothKeys()
    {
        var packed = new PorkbunDnsProvider(new MockHttpMessageHandler().ToHttpClient())
            .PackCredential(new Dictionary<string, string>
            {
                ["apiKey"] = " pk1_abc ",
                ["secretApiKey"] = " sk1_def "
            });

        var parsed = PorkbunCredentialStorage.TryParse(packed.Token);
        Assert.NotNull(parsed);
        Assert.Equal("pk1_abc", parsed!.ApiKey);
        Assert.Equal("sk1_def", parsed.SecretApiKey);
    }

    // Without its own discriminator a Porkbun payload would deserialize into an empty-fielded
    // Coolify credential and fail much later with a misleading error.
    [Fact]
    public void TryParse_RefusesAnotherProvidersPackedCredential()
    {
        var coolify = CoolifyCredentialStorage.Serialize("https://coolify.example.com", "token");

        Assert.Null(PorkbunCredentialStorage.TryParse(coolify));
    }

    [Theory]
    [InlineData("pk1_sb_abc", true)]
    [InlineData("pk1_live_abc", false)]
    [InlineData(null, false)]
    public void IsSandbox_ReadsThePrefix(string? apiKey, bool expected)
    {
        Assert.Equal(expected, PorkbunCredentialStorage.IsSandbox(apiKey));
    }
}
