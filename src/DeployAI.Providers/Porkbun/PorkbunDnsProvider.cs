using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Porkbun;

/// <summary>
/// Reads and writes DNS in a user's Porkbun account.
/// </summary>
/// <remarks>
/// Porkbun authenticates with a key <em>and</em> a secret, both in the JSON body of every request
/// rather than a header, so its credential is a packed pair rather than a bare token.
/// </remarks>
public sealed class PorkbunDnsProvider : IDnsZoneProvider
{
    internal const string ApiBase = "https://api.porkbun.com/api/json/v3";

    /// <summary>
    /// Porkbun's floor is set per account and is typically 600, so a shorter value is rejected
    /// rather than clamped. Longer than the 60 used elsewhere, which only means a record written
    /// during setup takes a little longer to be observable.
    /// </summary>
    private const int RecordTtlSeconds = 600;

    /// <summary>One page is up to 1000 domains; this is a runaway guard, not an expected limit.</summary>
    private const int MaxDomainPages = 10;

    private const int DomainPageSize = 1000;

    /// <summary>
    /// Porkbun is loose about whether a number is a number: <c>apiAccess</c> comes back as the
    /// string <c>"1"</c> from <c>domain/listAll</c>, which a strict reader rejects outright. Seen
    /// against the live API, and it would have surfaced as an unhandled parse failure rather than
    /// anything a user could act on, so every response here is read permissively.
    /// </summary>
    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;

    public PorkbunDnsProvider(HttpClient httpClient) => _httpClient = httpClient;

    public string ProviderName => "porkbun";

    public string DisplayName => "Porkbun";

    public IReadOnlyList<DnsCredentialField> CredentialFields { get; } =
    [
        new DnsCredentialField("apiKey", "API key", Secret: true, "pk1_…"),
        new DnsCredentialField("secretApiKey", "Secret API key", Secret: true, "sk1_…")
    ];

    public ProviderCredentials PackCredential(IReadOnlyDictionary<string, string> fields) =>
        new(PorkbunCredentialStorage.Serialize(
            fields.GetValueOrDefault("apiKey", string.Empty),
            fields.GetValueOrDefault("secretApiKey", string.Empty)));

    public async Task<DnsCredentialCheck> ValidateCredentialsAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        var keys = PorkbunCredentialStorage.TryParse(credentials.Token);
        if (keys is null)
        {
            return new DnsCredentialCheck(
                DnsCredentialVerdict.Malformed,
                "Enter both your Porkbun API key and secret API key.",
                []);
        }

        try
        {
            // /ping is the credential check and the sandbox check in one: it answers
            // credentialsValid and sandbox, so nothing has to ask the user which they pasted.
            var ping = await PostAsync<PorkbunPing>(keys, "ping", null, cancellationToken);
            if (ping?.CredentialsValid != true)
            {
                return new DnsCredentialCheck(
                    DnsCredentialVerdict.Rejected,
                    "Porkbun did not accept those keys. Check both values, or create a new pair.",
                    []);
            }

            var zones = await ListZonesInternalAsync(keys, cancellationToken);
            if (zones.Count == 0)
            {
                return new DnsCredentialCheck(
                    DnsCredentialVerdict.NoZonesVisible,
                    "These keys work, but there are no domains in this Porkbun account yet. " +
                    "Buy or transfer one first — no key can list a domain that is not there.",
                    []);
            }

            var ready = zones.Count(z => z.IsReady);
            var sandbox = PorkbunCredentialStorage.IsSandbox(keys.ApiKey) ? " (test mode — nothing here is real)" : string.Empty;

            return new DnsCredentialCheck(
                DnsCredentialVerdict.Ok,
                $"Connected{sandbox}. {ready} of {zones.Count} domains are ready to use.",
                zones);
        }
        catch (PorkbunApiException ex)
        {
            return new DnsCredentialCheck(ex.Verdict, ex.Message, [], RetryAfter: ex.RetryAfter);
        }
    }

    public async Task<IReadOnlyList<DnsZone>> ListZonesAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        var keys = RequireKeys(credentials);
        try
        {
            return await ListZonesInternalAsync(keys, cancellationToken);
        }
        catch (PorkbunApiException ex)
        {
            throw ex.ToDeployAIException("list your Porkbun domains");
        }
    }

    private async Task<List<DnsZone>> ListZonesInternalAsync(
        PorkbunCredentialStorage.StoredPorkbunCredentials keys, CancellationToken cancellationToken)
    {
        var zones = new List<DnsZone>();

        for (var page = 0; page < MaxDomainPages; page++)
        {
            var body = new Dictionary<string, object?> { ["start"] = page * DomainPageSize };
            var result = await PostAsync<PorkbunDomainList>(keys, "domain/listAll", body, cancellationToken);
            var batch = result?.Domains ?? [];

            foreach (var domain in batch.Where(d => !string.IsNullOrWhiteSpace(d.Domain)))
            {
                zones.Add(Describe(domain));
            }

            if (batch.Count < DomainPageSize)
            {
                break;
            }
        }

        return zones;
    }

    /// <summary>
    /// Turns a domain's registration status and API opt-in into whether DeployAI can use it.
    /// </summary>
    /// <remarks>
    /// Porkbun disables API access per domain by default, and a key with every permission still
    /// cannot touch a domain that has not been opted in. Because <c>listAll</c> reports the flag,
    /// this is answerable up front rather than discovered as an inexplicable failure later — which
    /// is precisely how the equivalent Cloudflare scoping trap wasted an afternoon.
    /// </remarks>
    private static DnsZone Describe(PorkbunDomain domain)
    {
        var name = domain.Domain!;
        var status = domain.Status?.Trim();
        var active = string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase);

        if (active && domain.ApiAccess == 0)
        {
            return new DnsZone(
                name, name, CanWrite: false, DnsZoneUsability.ReadOnly,
                $"API access is switched off for {name}. In Porkbun, open Domain Management → " +
                $"{name} → Details and turn on API Access — a key alone is not enough.");
        }

        if (active)
        {
            return new DnsZone(
                name, name, CanWrite: true, DnsZoneUsability.Ready,
                $"Ready — DeployAI can point names in {name} at your server.");
        }

        return new DnsZone(
            name, name, CanWrite: null, DnsZoneUsability.Unknown,
            $"{name} is in an unexpected state at Porkbun" +
            (string.IsNullOrWhiteSpace(status) ? "." : $" (\"{status}\")."));
    }

    public async Task<DnsRecordWrite> UpsertAddressRecordAsync(
        ProviderCredentials credentials,
        string zoneId,
        string hostname,
        string address,
        CancellationToken cancellationToken)
    {
        var keys = RequireKeys(credentials);
        var subdomain = ToSubdomain(zoneId, hostname);

        try
        {
            var existing = await PostAsync<PorkbunRecordList>(
                keys, $"dns/retrieveByNameType/{zoneId}/A/{subdomain}", null, cancellationToken);
            var current = existing?.Records?.FirstOrDefault();

            if (current is null)
            {
                var created = await PostAsync<PorkbunCreatedRecord>(
                    keys, $"dns/create/{zoneId}",
                    new Dictionary<string, object?>
                    {
                        // The label only. Porkbun prefixes the zone itself, so sending the full
                        // name here produces app.example.com.example.com — silently, and the
                        // opposite of what Cloudflare's API wants for the same operation.
                        ["name"] = subdomain,
                        ["type"] = "A",
                        ["content"] = address,
                        ["ttl"] = RecordTtlSeconds
                    },
                    cancellationToken);

                return new DnsRecordWrite(created?.Id ?? string.Empty, Created: true);
            }

            await PostAsync<object>(
                keys, $"dns/editByNameType/{zoneId}/A/{subdomain}",
                new Dictionary<string, object?> { ["content"] = address, ["ttl"] = RecordTtlSeconds },
                cancellationToken);

            return new DnsRecordWrite(current.Id ?? string.Empty, Created: false);
        }
        catch (PorkbunApiException ex)
        {
            throw ex.ToDeployAIException($"point {hostname} at {address}");
        }
    }

    /// <summary>
    /// The label Porkbun wants: the hostname with the zone removed, or empty for the zone itself.
    /// </summary>
    internal static string ToSubdomain(string zone, string hostname)
    {
        var z = zone.Trim().Trim('.');
        var h = hostname.Trim().Trim('.');

        if (string.Equals(h, z, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return h.EndsWith($".{z}", StringComparison.OrdinalIgnoreCase)
            ? h[..^(z.Length + 1)]
            : h;
    }

    public async Task<bool> DeleteRecordAsync(
        ProviderCredentials credentials,
        string zoneId,
        string recordId,
        CancellationToken cancellationToken)
    {
        var keys = PorkbunCredentialStorage.TryParse(credentials.Token);
        if (keys is null)
        {
            return false;
        }

        try
        {
            await PostAsync<object>(keys, $"dns/delete/{zoneId}/{recordId}", null, cancellationToken);
            return true;
        }
        catch (PorkbunApiException)
        {
            // A record that is already gone is the outcome the caller wanted; anything else is
            // logged by the caller and must not block a disconnect.
            return false;
        }
    }

    private static PorkbunCredentialStorage.StoredPorkbunCredentials RequireKeys(
        ProviderCredentials credentials) =>
        PorkbunCredentialStorage.TryParse(credentials.Token)
        ?? throw new DeployAIException(
            "porkbun_credentials_invalid",
            "Your Porkbun connection is missing its keys. Reconnect it in settings.");

    /// <summary>
    /// Every Porkbun call is a POST carrying the key pair in its body, and every response is a
    /// <c>status</c> of SUCCESS or ERROR — the HTTP code alone does not decide it.
    /// </summary>
    private async Task<T?> PostAsync<T>(
        PorkbunCredentialStorage.StoredPorkbunCredentials keys,
        string path,
        Dictionary<string, object?>? body,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>(body ?? [])
        {
            ["apikey"] = keys.ApiKey,
            ["secretapikey"] = keys.SecretApiKey
        };

        HttpResponseMessage response;
        string raw;
        try
        {
            response = await _httpClient.PostAsync(
                $"{ApiBase}/{path}", JsonContent.Create(payload), cancellationToken);
            raw = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            throw new PorkbunApiException(
                DnsCredentialVerdict.Unreachable,
                "We could not reach Porkbun just now, so nothing was checked or changed. Try again in a moment.");
        }

        PorkbunEnvelope? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<PorkbunEnvelope>(raw, ResponseJson);
        }
        catch (JsonException)
        {
            // Falls through to the status handling below.
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            throw new PorkbunApiException(
                DnsCredentialVerdict.RateLimited,
                "Porkbun is limiting requests for this account right now. Nothing was changed — " +
                (retryAfter is { TotalSeconds: > 0 }
                    ? $"try again in about {(int)retryAfter.Value.TotalSeconds} seconds."
                    : "try again shortly."),
                retryAfter);
        }

        var failed = !response.IsSuccessStatusCode ||
                     !string.Equals(envelope?.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

        if (failed)
        {
            if ((int)response.StatusCode >= 500)
            {
                throw new PorkbunApiException(
                    DnsCredentialVerdict.Unreachable,
                    "Porkbun returned an error of its own, so nothing was changed. Try again in a moment.");
            }

            throw new PorkbunApiException(
                DnsCredentialVerdict.Rejected,
                string.IsNullOrWhiteSpace(envelope?.Message)
                    ? $"Porkbun refused that request ({(int)response.StatusCode})."
                    : envelope.Message);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, ResponseJson);
        }
        catch (JsonException)
        {
            // A success envelope whose body is a shape we do not recognise is still not the
            // caller's fault, and must not escape as an unhandled parse failure.
            throw new PorkbunApiException(
                DnsCredentialVerdict.Unreachable,
                "Porkbun answered in a form DeployAI could not read, so nothing was changed.");
        }
    }

    private sealed class PorkbunApiException : Exception
    {
        public PorkbunApiException(DnsCredentialVerdict verdict, string message, TimeSpan? retryAfter = null)
            : base(message)
        {
            Verdict = verdict;
            RetryAfter = retryAfter;
        }

        public DnsCredentialVerdict Verdict { get; }

        public TimeSpan? RetryAfter { get; }

        public DeployAIException ToDeployAIException(string attempted) => Verdict switch
        {
            DnsCredentialVerdict.RateLimited => new DeployAIException(DnsErrorCodes.RateLimited, Message),
            DnsCredentialVerdict.Unreachable => new DeployAIException(DnsErrorCodes.Unreachable, Message),
            _ => new DeployAIException("porkbun_api_error", $"Could not {attempted}. {Message}")
        };
    }

    /// <summary>
    /// Reads a value that may arrive as either a JSON string or a JSON number, and yields a string.
    /// </summary>
    /// <remarks>
    /// Porkbun is inconsistent about this within a single resource: <c>dns/create</c> returns a
    /// record id as the number <c>534057924</c>, while <c>dns/retrieveByNameType</c> returns the
    /// very same id as the string <c>"534057924"</c>. Both were observed against the live API, and
    /// a strict reader turns a record that was created perfectly well into a reported failure.
    /// </remarks>
    private sealed class FlexibleStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out var l)
                    ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonTokenType.Null => null,
                _ => null
            };

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    private sealed class PorkbunEnvelope
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }

    private sealed class PorkbunPing
    {
        [JsonPropertyName("credentialsValid")]
        public bool? CredentialsValid { get; set; }

        [JsonPropertyName("sandbox")]
        public bool Sandbox { get; set; }
    }

    private sealed class PorkbunDomainList
    {
        [JsonPropertyName("domains")]
        public List<PorkbunDomain>? Domains { get; set; }
    }

    private sealed class PorkbunDomain
    {
        [JsonPropertyName("domain")]
        public string? Domain { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        /// <summary>1 when the domain is opted in to API access. Off by default.</summary>
        [JsonPropertyName("apiAccess")]
        public int ApiAccess { get; set; }
    }

    private sealed class PorkbunRecordList
    {
        [JsonPropertyName("records")]
        public List<PorkbunRecord>? Records { get; set; }
    }

    private sealed class PorkbunRecord
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? Id { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class PorkbunCreatedRecord
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? Id { get; set; }
    }
}
