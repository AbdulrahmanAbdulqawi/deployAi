using System.Net;
using System.Text.Json;
using DeployAI.Core.Domains;
using DeployAI.Core.Providers;
using DeployAI.Providers.Porkbun;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

/// <summary>
/// Pricing and buying, against the payloads Porkbun actually returns.
/// </summary>
/// <remarks>
/// Every fixture below was captured from the live API. The two refusals matter most: a cost that
/// does not match what was quoted, and a missing agreement to the registration terms. Between them
/// they are why a user cannot be charged a figure they were never shown.
/// </remarks>
public class PorkbunRegistrarTests
{
    private const string Api = "https://api.porkbun.com/api/json/v3";
    private const string Hostname = "yemenconnect.com";

    private static readonly ProviderCredentials Credentials =
        new(PorkbunCredentialStorage.Serialize("pk1_live_key", "sk1_live_secret"));

    private static PorkbunRegistrar Registrar(MockHttpMessageHandler handler) =>
        new(handler.ToHttpClient());

    private const string AvailableJson = """
        {"status":"SUCCESS","response":{"avail":"yes","type":"registration","price":"11.08",
         "firstYearPromo":"no","regularPrice":"11.08","premium":"no",
         "additional":{"renewal":{"type":"renewal","price":"11.08","regularPrice":"11.08"}},
         "minDuration":1}}
        """;

    // ---- pricing ---------------------------------------------------------

    [Fact]
    public async Task CheckAvailabilityAsync_PricesAnAvailableDomainInCents()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/checkDomain/{Hostname}")
            .Respond(HttpStatusCode.OK, "application/json", AvailableJson);

        var offer = await Registrar(handler).CheckAvailabilityAsync(
            Credentials, Hostname, CancellationToken.None);

        Assert.Equal(DomainAvailability.Available, offer.Availability);
        Assert.Equal(1108, offer.Price!.FirstYearCents);
        Assert.Equal(1108, offer.Price.RenewalCents);
        Assert.False(offer.Price.IsPremium);
    }

    // A taken domain answers SUCCESS with avail "no" -- not an error. Treating it as one would make
    // the commonest search outcome look like a fault.
    [Fact]
    public async Task CheckAvailabilityAsync_ReportsATakenDomainPlainly()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/checkDomain/google.com")
            .Respond(HttpStatusCode.OK, "application/json",
                """{"status":"SUCCESS","response":{"avail":"no","price":"11.08","minDuration":1}}""");

        var offer = await Registrar(handler).CheckAvailabilityAsync(
            Credentials, "google.com", CancellationToken.None);

        Assert.Equal(DomainAvailability.Taken, offer.Availability);
        Assert.Null(offer.Price);
    }

    // The renewal is the figure people are surprised by, and a promotional first year is exactly
    // when they are least likely to go looking for it.
    [Fact]
    public async Task CheckAvailabilityAsync_SaysTheRenewalPrice_WhenTheFirstYearIsAPromotion()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/checkDomain/cheap.xyz")
            .Respond(HttpStatusCode.OK, "application/json", """
                {"status":"SUCCESS","response":{"avail":"yes","price":"1.99","firstYearPromo":"yes",
                 "regularPrice":"12.99","premium":"no","minDuration":1,
                 "additional":{"renewal":{"price":"12.99"}}}}
                """);

        var offer = await Registrar(handler).CheckAvailabilityAsync(
            Credentials, "cheap.xyz", CancellationToken.None);

        Assert.Equal(199, offer.Price!.FirstYearCents);
        Assert.Equal(1299, offer.Price.RenewalCents);
        Assert.True(offer.Price.IsFirstYearPromotional);
        Assert.Contains("promotional", offer.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$12.99", offer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAvailabilityAsync_CallsOutAPremiumName()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/checkDomain/gold.com")
            .Respond(HttpStatusCode.OK, "application/json", """
                {"status":"SUCCESS","response":{"avail":"yes","price":"4999.00","firstYearPromo":"no",
                 "premium":"yes","minDuration":1,"additional":{"renewal":{"price":"4999.00"}}}}
                """);

        var offer = await Registrar(handler).CheckAvailabilityAsync(
            Credentials, "gold.com", CancellationToken.None);

        Assert.True(offer.Price!.IsPremium);
        Assert.Equal(499900, offer.Price.FirstYearCents);
        Assert.Contains("premium", offer.Message, StringComparison.OrdinalIgnoreCase);
    }

    // One check per ten seconds, and the response says how long is left -- so the message can be
    // specific rather than telling someone to try again "shortly".
    [Fact]
    public async Task CheckAvailabilityAsync_SaysHowLongToWait_WhenThrottled()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/checkDomain/{Hostname}")
            .Respond(HttpStatusCode.TooManyRequests, "application/json",
                """{"status":"ERROR","code":"RATE_LIMIT_EXCEEDED","message":"1 of 1 used.","ttlRemaining":9}""");

        var offer = await Registrar(handler).CheckAvailabilityAsync(
            Credentials, Hostname, CancellationToken.None);

        Assert.Equal(DomainAvailability.Unknown, offer.Availability);
        Assert.Contains("9 seconds", offer.Message, StringComparison.Ordinal);
    }

    // ---- buying ----------------------------------------------------------

    [Fact]
    public async Task RegisterAsync_RestatesTheQuotedCostAndAgreesToTheTerms()
    {
        var handler = new MockHttpMessageHandler();
        JsonElement? body = null;
        string? idempotencyKey = null;
        handler.When(HttpMethod.Post, $"{Api}/domain/create/{Hostname}").Respond(async call =>
        {
            body = JsonDocument.Parse(await call.Content!.ReadAsStringAsync()).RootElement.Clone();
            idempotencyKey = call.Headers.TryGetValues("Idempotency-Key", out var v) ? v.First() : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"SUCCESS","domain":"yemenconnect.com","cost":1108,"orderId":9912355}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });

        var result = await Registrar(handler).RegisterAsync(
            Credentials, Hostname, 1108, "quote-abc", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1108, body!.Value.GetProperty("cost").GetInt32());
        Assert.Equal("yes", body.Value.GetProperty("agreeToTerms").GetString());
        Assert.False(body.Value.TryGetProperty("dryRun", out _));
        // Without this a timeout leaves nobody able to say whether the domain was bought.
        Assert.Equal("quote-abc", idempotencyKey);
    }

    // The order id comes back as a number where other ids come back as strings.
    [Fact]
    public async Task RegisterAsync_ReadsTheOrderId_WhetherNumberOrString()
    {
        foreach (var orderJson in new[] { "9912355", "\"9912355\"" })
        {
            var handler = new MockHttpMessageHandler();
            handler.When(HttpMethod.Post, $"{Api}/domain/create/{Hostname}").Respond(
                HttpStatusCode.OK, "application/json",
                "{\"status\":\"SUCCESS\",\"cost\":1108,\"orderId\":" + orderJson + "}");

            var result = await Registrar(handler).RegisterAsync(
                Credentials, Hostname, 1108, "quote-abc", CancellationToken.None);

            Assert.Equal("9912355", result.OrderId);
        }
    }

    // Captured live. This is the registrar's own guarantee that a user cannot be charged a figure
    // they were never shown, and it holds even if DeployAI's stored quote were wrong.
    [Fact]
    public async Task RegisterAsync_FailsWhenTheCostDoesNotMatchTheRegistrarsOwn()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/create/{Hostname}").Respond(
            HttpStatusCode.BadRequest, "application/json",
            """
            {"status":"ERROR","message":"The cost submitted must equal the cost of the domain for it's minimum allowed duration.","code":"THE_COST_SUBMITTED_MUST_EQUAL_THE_COST_OF_THE_DOMAIN_FOR_ITS_MINIMUM_ALLOWED_DURATION"}
            """);

        var result = await Registrar(handler).RegisterAsync(
            Credentials, Hostname, 100, "quote-abc", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("must equal the cost", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.OrderId);
    }

    [Fact]
    public async Task RegisterAsync_FailsWhenTheRegistrarWantsTermsAgreed()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/create/{Hostname}").Respond(
            HttpStatusCode.BadRequest, "application/json",
            """{"status":"ERROR","message":"You must agree to the Domain Name Registration Agreement...","code":"TERMS_NOT_AGREED"}""");

        var result = await Registrar(handler).RegisterAsync(
            Credentials, Hostname, 1108, "quote-abc", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DryRunAsync_ValidatesWithoutBuying()
    {
        var handler = new MockHttpMessageHandler();
        JsonElement? body = null;
        handler.When(HttpMethod.Post, $"{Api}/domain/create/{Hostname}").Respond(async call =>
        {
            body = JsonDocument.Parse(await call.Content!.ReadAsStringAsync()).RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"status":"SUCCESS","dryRun":true,"wouldSucceed":true,"domain":"yemenconnect.com",
                     "cost":1108,"costDisplay":"$11.08","balance":100000,"sufficientFunds":true,
                     "message":"Dry run: this registration would succeed and cost $11.08."}
                    """,
                    System.Text.Encoding.UTF8, "application/json")
            };
        });

        var result = await Registrar(handler).DryRunAsync(
            Credentials, Hostname, 1108, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(body!.Value.GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public async Task DryRunAsync_ReportsWhenItWouldNotSucceed()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/create/{Hostname}").Respond(
            HttpStatusCode.OK, "application/json",
            """{"status":"SUCCESS","dryRun":true,"wouldSucceed":false,"message":"Insufficient funds."}""");

        var result = await Registrar(handler).DryRunAsync(
            Credentials, Hostname, 1108, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Insufficient", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The one failure where the honest answer is "we do not know". Retrying blind could pay twice.
    [Fact]
    public async Task RegisterAsync_WarnsAgainstBlindRetry_WhenTheRegistrarCannotBeReached()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{Api}/domain/create/{Hostname}")
            .Throw(new HttpRequestException("connection reset"));

        var result = await Registrar(handler).RegisterAsync(
            Credentials, Hostname, 1108, "quote-abc", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("twice", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
