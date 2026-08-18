using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Porkbun;

/// <summary>
/// Buys domains through the user's own Porkbun account.
/// </summary>
/// <remarks>
/// Porkbun's registration endpoint is unusually well suited to being driven by software: the cost
/// must be restated in pennies and is rejected if it does not match, agreement to the registration
/// terms is an explicit per-purchase flag, a dry run validates availability, price, eligibility and
/// funds without charging, and an idempotency key makes a retry safe for 24 hours. All four were
/// confirmed against the live API rather than taken from the specification.
/// </remarks>
public sealed class PorkbunRegistrar : IDomainRegistrar
{
    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;

    public PorkbunRegistrar(HttpClient httpClient) => _httpClient = httpClient;

    public string ProviderName => "porkbun";

    public string DisplayName => "Porkbun";

    public async Task<DomainOffer> CheckAvailabilityAsync(
        ProviderCredentials credentials, string hostname, CancellationToken cancellationToken)
    {
        var keys = RequireKeys(credentials);

        PorkbunAvailability? result;
        try
        {
            result = await PostAsync<PorkbunAvailability>(
                keys, $"domain/checkDomain/{hostname}", null, cancellationToken);
        }
        catch (PorkbunRegistrarException ex)
        {
            // A search that could not run is not a domain that is unavailable, and it is certainly
            // not an error page. Porkbun allows one check every ten seconds, so being throttled
            // here is ordinary rather than exceptional.
            return new DomainOffer(hostname, DomainAvailability.Unknown, null, ex.Message);
        }

        var response = result?.Response;

        if (response is null)
        {
            return new DomainOffer(
                hostname, DomainAvailability.Unknown, null,
                $"Porkbun did not say whether {hostname} is available.");
        }

        if (!string.Equals(response.Available, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainOffer(
                hostname, DomainAvailability.Taken, null, $"{hostname} is already taken.");
        }

        var firstYear = ToCents(response.Price);
        var renewal = ToCents(response.Additional?.Renewal?.Price) ?? firstYear;

        if (firstYear is null)
        {
            return new DomainOffer(
                hostname, DomainAvailability.Unsupported, null,
                $"{hostname} is available but Porkbun did not price it, so it cannot be bought here.");
        }

        var premium = string.Equals(response.Premium, "yes", StringComparison.OrdinalIgnoreCase);
        var promotional = string.Equals(response.FirstYearPromo, "yes", StringComparison.OrdinalIgnoreCase);

        return new DomainOffer(
            hostname,
            DomainAvailability.Available,
            new DomainPrice(firstYear.Value, renewal!.Value, promotional, premium,
                Math.Max(1, response.MinDuration)),
            Describe(hostname, firstYear.Value, renewal.Value, promotional, premium));
    }

    private static string Describe(
        string hostname, int firstYear, int renewal, bool promotional, bool premium)
    {
        var text = $"{hostname} is available for {Money(firstYear)}";

        if (premium)
        {
            text += " — a premium name, priced by the registry rather than the registrar";
        }

        // Said plainly, because the renewal is the number people are surprised by, and a
        // promotional first year is exactly when they are least likely to look for it.
        text += renewal == firstYear
            ? $", and {Money(renewal)} a year after that."
            : promotional
                ? $" for the first year, then {Money(renewal)} a year — the first year is a promotional price."
                : $", then {Money(renewal)} a year.";

        return text;
    }

    private static string Money(int cents) =>
        (cents / 100m).ToString("C", CultureInfo.GetCultureInfo("en-US"));

    public Task<DomainRegistration> DryRunAsync(
        ProviderCredentials credentials,
        string hostname,
        int expectedCostCents,
        CancellationToken cancellationToken) =>
        CreateAsync(credentials, hostname, expectedCostCents, null, dryRun: true, cancellationToken);

    public Task<DomainRegistration> RegisterAsync(
        ProviderCredentials credentials,
        string hostname,
        int expectedCostCents,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        CreateAsync(credentials, hostname, expectedCostCents, idempotencyKey, dryRun: false, cancellationToken);

    private async Task<DomainRegistration> CreateAsync(
        ProviderCredentials credentials,
        string hostname,
        int expectedCostCents,
        string? idempotencyKey,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var keys = RequireKeys(credentials);

        var body = new Dictionary<string, object?>
        {
            // Restated so the registrar can refuse a mismatch. This is the guarantee that a user
            // cannot be charged a figure they were never shown.
            ["cost"] = expectedCostCents,
            ["agreeToTerms"] = "yes"
        };

        if (dryRun)
        {
            body["dryRun"] = true;
        }

        try
        {
            var result = await PostAsync<PorkbunRegistration>(
                keys, $"domain/create/{hostname}", body, cancellationToken, idempotencyKey);

            return new DomainRegistration(
                Succeeded: dryRun ? result?.WouldSucceed ?? false : true,
                hostname,
                result?.OrderId,
                result?.Cost ?? expectedCostCents,
                result?.Message ?? (dryRun
                    ? $"{hostname} can be registered for {Money(expectedCostCents)}."
                    : $"{hostname} is yours."));
        }
        catch (PorkbunRegistrarException ex)
        {
            return new DomainRegistration(false, hostname, null, null, ex.Message);
        }
    }

    private static int? ToCents(string? dollars) =>
        decimal.TryParse(dollars, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Round(value * 100m, MidpointRounding.AwayFromZero)
            : null;

    private static PorkbunCredentialStorage.StoredPorkbunCredentials RequireKeys(
        ProviderCredentials credentials) =>
        PorkbunCredentialStorage.TryParse(credentials.Token)
        ?? throw new DeployAIException(
            "porkbun_credentials_invalid",
            "Your Porkbun connection is missing its keys. Reconnect it in settings.");

    private async Task<T?> PostAsync<T>(
        PorkbunCredentialStorage.StoredPorkbunCredentials keys,
        string path,
        Dictionary<string, object?>? body,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        var payload = new Dictionary<string, object?>(body ?? [])
        {
            ["apikey"] = keys.ApiKey,
            ["secretapikey"] = keys.SecretApiKey
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PorkbunDnsProvider.ApiBase}/{path}")
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Replayed for 24 hours, so a retry after a timeout returns the original outcome
            // rather than buying the domain a second time.
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        HttpResponseMessage response;
        string raw;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
            raw = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            throw new PorkbunRegistrarException(
                "We could not reach Porkbun, so nothing was bought. Check before trying again — " +
                "if the order did go through, buying again would charge you twice.",
                DnsErrorCodes.Unreachable);
        }

        PorkbunError? error = null;
        try
        {
            error = JsonSerializer.Deserialize<PorkbunError>(raw, ResponseJson);
        }
        catch (JsonException)
        {
            // Handled below.
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Availability checks allow one every ten seconds, which is easy to hit and which the
            // response itself quantifies — so say how long rather than "shortly".
            var wait = error?.TtlRemaining;
            throw new PorkbunRegistrarException(
                "Porkbun only allows one domain check every few seconds. Nothing was bought — " +
                (wait is > 0 ? $"try again in {wait} seconds." : "try again in a moment."),
                DnsErrorCodes.RateLimited);
        }

        if (!response.IsSuccessStatusCode ||
            !string.Equals(error?.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            throw new PorkbunRegistrarException(
                string.IsNullOrWhiteSpace(error?.Message)
                    ? $"Porkbun refused that request ({(int)response.StatusCode})."
                    : error.Message,
                "porkbun_api_error");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, ResponseJson);
        }
        catch (JsonException)
        {
            throw new PorkbunRegistrarException(
                "Porkbun answered in a form DeployAI could not read.", "porkbun_api_error");
        }
    }

    private sealed class PorkbunRegistrarException : Exception
    {
        public PorkbunRegistrarException(string message, string errorCode) : base(message) =>
            ErrorCode = errorCode;

        public string ErrorCode { get; }
    }

    private sealed class PorkbunError
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        /// <summary>Seconds until the rate-limit window resets. Present on a throttled response.</summary>
        [JsonPropertyName("ttlRemaining")]
        public int? TtlRemaining { get; set; }
    }

    private sealed class PorkbunAvailability
    {
        [JsonPropertyName("response")]
        public PorkbunAvailabilityDetail? Response { get; set; }
    }

    private sealed class PorkbunAvailabilityDetail
    {
        [JsonPropertyName("avail")]
        public string? Available { get; set; }

        [JsonPropertyName("price")]
        public string? Price { get; set; }

        [JsonPropertyName("firstYearPromo")]
        public string? FirstYearPromo { get; set; }

        [JsonPropertyName("regularPrice")]
        public string? RegularPrice { get; set; }

        [JsonPropertyName("premium")]
        public string? Premium { get; set; }

        [JsonPropertyName("minDuration")]
        public int MinDuration { get; set; }

        [JsonPropertyName("additional")]
        public PorkbunAdditionalPricing? Additional { get; set; }
    }

    private sealed class PorkbunAdditionalPricing
    {
        [JsonPropertyName("renewal")]
        public PorkbunPriceEntry? Renewal { get; set; }
    }

    private sealed class PorkbunPriceEntry
    {
        [JsonPropertyName("price")]
        public string? Price { get; set; }
    }

    private sealed class PorkbunRegistration
    {
        [JsonPropertyName("orderId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? OrderId { get; set; }

        [JsonPropertyName("cost")]
        public int? Cost { get; set; }

        [JsonPropertyName("wouldSucceed")]
        public bool? WouldSucceed { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Porkbun returns an order id as a number and other ids as strings; read either.
    /// </summary>
    private sealed class FlexibleStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out var l)
                    ? l.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
                _ => null
            };

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
