using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Cloudflare;

/// <summary>
/// Writes A records into a user's Cloudflare account so pointing a domain at their server stops
/// being something they have to do in someone else's dashboard.
/// </summary>
/// <remarks>
/// Credentials are a scoped API token, parsed per call and turned into a bearer header — the same
/// shape as every other provider here, and the reason this class can be constructed in a test with
/// nothing but an <see cref="HttpClient"/>.
/// </remarks>
public sealed class CloudflareDnsProvider : IDnsZoneProvider
{
    /// <summary>
    /// One minute. Short on purpose: a record written during setup is polled for within seconds,
    /// and a long TTL would have resolvers caching its absence for the length of the deadline.
    /// Also the lowest value Cloudflare actually accepts — there is no legal TTL between 2 and 59,
    /// 30 is Enterprise-only, and 1 means "automatic" (300s), which is not what is wanted here.
    /// </summary>
    private const int RecordTtlSeconds = 60;

    /// <summary>Cloudflare's maximum page size for zones. Asking for more is rejected, not clamped.</summary>
    private const int ZonePageSize = 50;

    /// <summary>A runaway guard, not an expected limit — 20 pages is 1000 zones.</summary>
    private const int MaxZonePages = 20;

    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    private readonly HttpClient _httpClient;

    public CloudflareDnsProvider(HttpClient httpClient) => _httpClient = httpClient;

    public string ProviderName => "cloudflare";

    public string DisplayName => "Cloudflare";

    public IReadOnlyList<DnsCredentialField> CredentialFields { get; } =
        [new DnsCredentialField("token", "API token", Secret: true, "Paste your token")];

    /// <summary>A single bearer token, stored as-is — there is nothing to pack.</summary>
    public ProviderCredentials PackCredential(IReadOnlyDictionary<string, string> fields) =>
        new(fields.TryGetValue("token", out var token) ? token.Trim() : string.Empty);

    /// <summary>
    /// Checks a token by listing the account's zones.
    /// </summary>
    /// <remarks>
    /// Listing zones is the gate rather than Cloudflare's own token-verify endpoint, and that is
    /// deliberate. <c>GET /user/tokens/verify</c> only answers for *user* tokens; an account-owned
    /// token — the kind Cloudflare recommends for durable service integrations — verifies at
    /// <c>/accounts/{id}/tokens/verify</c> and returns a flat 401 on the user path despite being
    /// perfectly valid. Gating on verify would reject the better sort of token and demand an
    /// account id nothing else here needs. Listing zones works for both, and proves the one thing
    /// the managed path actually requires: turning a hostname into a zone id.
    /// </remarks>
    public async Task<DnsCredentialCheck> ValidateCredentialsAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        var token = credentials.Token?.Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            return Refuse(DnsCredentialVerdict.Malformed, "Enter your Cloudflare API token.");
        }

        // Refused before any network call, because neither can ever work here. The global key is
        // account-wide and authenticates with an email/key header pair rather than a bearer token;
        // the Origin CA key uses its own header and its authentication scheme reaches end of life
        // in September 2026.
        if (token.StartsWith("cfk_", StringComparison.Ordinal))
        {
            return Refuse(
                DnsCredentialVerdict.Malformed,
                "That looks like a Global API Key, which gives access to your whole Cloudflare account. " +
                "Create an API token instead, scoped to just the domains you want DeployAI to manage.");
        }

        if (token.StartsWith("v1.0-", StringComparison.Ordinal))
        {
            return Refuse(
                DnsCredentialVerdict.Malformed,
                "That looks like an Origin CA key, which cannot be used for DNS. Create an API token instead.");
        }

        try
        {
            var zones = await ListZonesInternalAsync(credentials, cancellationToken);

            // A token scoped too narrowly answers 200 with an empty list rather than refusing, so
            // an empty result is a distinct outcome and not a success.
            if (zones.Count == 0)
            {
                // The message below lists three causes because this call cannot tell them apart.
                // token/verify can, for the only part that matters to storage: whether the token
                // itself is good. An empty account with a working token is worth keeping; a token
                // that cannot be verified is not.
                return new DnsCredentialCheck(
                    DnsCredentialVerdict.NoZonesVisible,
                    await DescribeEmptyZoneListAsync(credentials, cancellationToken),
                    [],
                    CredentialsProven: await TokenAuthenticatesAsync(credentials, cancellationToken));
            }

            return new DnsCredentialCheck(
                DnsCredentialVerdict.Ok,
                $"Connected. {zones.Count(z => z.IsReady)} of {zones.Count} domains are ready to use.",
                zones,
                await TryReadExpiryAsync(credentials, cancellationToken));
        }
        catch (CloudflareApiException ex)
        {
            return new DnsCredentialCheck(ex.Verdict, ex.Message, [], RetryAfter: ex.RetryAfter);
        }
    }

    private static DnsCredentialCheck Refuse(DnsCredentialVerdict verdict, string message) =>
        new(verdict, message, []);

    /// <summary>
    /// Explains an empty zone list, naming the account when that can be discovered.
    /// </summary>
    /// <remarks>
    /// The obvious reading — "this account has no domains" — is usually wrong. A token authenticates
    /// and is allowed to list, yet returns nothing, when its <em>Zone Resources</em> do not include
    /// the user's domains; and Cloudflare's own "Edit zone DNS" template defaults that to a single
    /// zone the user has to pick. So scope is named first, and the wrong-account case second.
    /// <para>
    /// Naming the account turns "check you used the right one" — which the user has no way to
    /// check from here — into something they can confirm at a glance.
    /// </para>
    /// </remarks>
    private async Task<string> DescribeEmptyZoneListAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        var account = await TryReadAccountNameAsync(credentials, cancellationToken);

        return
            "This token works, but DeployAI cannot see any domains through it" +
            (account is null ? "." : $" on the \"{account}\" Cloudflare account.") +
            " Three things cause that, and it is worth checking them in this order. " +
            "First: does your Cloudflare dashboard list any domains at all? If the Websites list is " +
            "empty, no token can help — the domain has to be added to Cloudflare first, and its " +
            "nameservers pointed there from wherever you bought it. " +
            "Second: the token's permissions may have been added under \"Account\" rather than " +
            "\"Zone\" — only Zone → Read can list domains, and the Account group's DNS section cannot. " +
            "Third: Zone Resources may not include them, where \"Include → All zones\" is simplest." +
            (account is null
                ? " If you have more than one Cloudflare account, also check the token was created under the one your domains are in."
                : string.Empty);
    }

    /// <summary>
    /// Best-effort read of which account a token belongs to. A zone-scoped token cannot list
    /// accounts at all, so failure here is expected and simply means the name goes unmentioned.
    /// </summary>
    private async Task<string?> TryReadAccountNameAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, credentials, "accounts?per_page=1");
            var result = await SendAsync<List<CloudflareAccount>>(request, cancellationToken);
            return result.Value?.FirstOrDefault()?.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort read of when the token stops working, so the user can be warned before it does
    /// rather than after.
    /// </summary>
    /// <remarks>
    /// Deliberately cannot fail the connection. This endpoint answers only for *user* tokens; an
    /// account-owned token verifies at a different path and returns a flat 401 here despite being
    /// entirely valid. Treating that as a problem would reject the kind of token Cloudflare
    /// recommends — so a failure here means "no expiry known", never "bad token".
    /// </remarks>
    /// <summary>
    /// Whether Cloudflare confirms the token authenticates, whatever it happens to be scoped to see.
    /// </summary>
    /// <remarks>
    /// Unlike the zone listing, this asks about the token rather than about the account, so it
    /// separates "nothing is there" from "this cannot see what is there" — which the empty list
    /// alone never can. Anything other than an active confirmation is treated as unproven: could
    /// not check is not the same as checked and fine, and this is the flag that decides whether a
    /// credential gets stored.
    /// </remarks>
    private async Task<bool> TokenAuthenticatesAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, credentials, "user/tokens/verify");
            var result = await SendAsync<CloudflareTokenStatus>(request, cancellationToken);
            return string.Equals(result.Value?.Status, "active", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<DateTimeOffset?> TryReadExpiryAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, credentials, "user/tokens/verify");
            var result = await SendAsync<CloudflareTokenStatus>(request, cancellationToken);
            return result.Value?.ExpiresOn;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any failure at all means "no expiry known". Narrowing this would let an unexpected
            // shape from a diagnostic call sink a connection that has already been proven to work.
            return null;
        }
    }

    public async Task<IReadOnlyList<DnsZone>> ListZonesAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            return await ListZonesInternalAsync(credentials, cancellationToken);
        }
        catch (CloudflareApiException ex)
        {
            throw ex.ToDeployAIException("list your Cloudflare domains");
        }
    }

    /// <summary>
    /// Reads every page. Cloudflare caps a page at 50 zones, so a single request silently loses
    /// everything after the fiftieth — and the domain whose zone was cut off would fall back to
    /// asking the user to add a record by hand, with nothing explaining why.
    /// </summary>
    private async Task<List<DnsZone>> ListZonesInternalAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        var zones = new List<DnsZone>();

        for (var page = 1; page <= MaxZonePages; page++)
        {
            using var request = CreateRequest(
                HttpMethod.Get, credentials, $"zones?per_page={ZonePageSize}&page={page}");
            var result = await SendAsync<List<CloudflareZone>>(request, cancellationToken);

            foreach (var zone in result.Value ?? [])
            {
                if (!string.IsNullOrWhiteSpace(zone.Id) && !string.IsNullOrWhiteSpace(zone.Name))
                {
                    zones.Add(Describe(zone));
                }
            }

            var totalPages = result.Info?.TotalPages ?? 1;
            if (page >= totalPages)
            {
                break;
            }
        }

        return zones;
    }

    /// <summary>
    /// Turns a zone's status and type into whether records written there would actually resolve.
    /// </summary>
    /// <remarks>
    /// Derived from state rather than from the zone's <c>permissions</c> array, which Cloudflare
    /// documents as legacy user-membership data and does not populate under token authentication —
    /// reading it produced a flag that was always true and therefore meaningless.
    /// <para>
    /// <c>paused</c> is deliberately ignored: it turns off proxying, not DNS resolution, so
    /// treating it as unhealthy would false-alarm on every DNS-only zone, which is exactly the kind
    /// DeployAI wants.
    /// </para>
    /// </remarks>
    private static DnsZone Describe(CloudflareZone zone)
    {
        var name = zone.Name!;
        var status = zone.Status?.Trim().ToLowerInvariant();
        var type = zone.Type?.Trim().ToLowerInvariant();
        var account = zone.Account?.Name;

        // Checked before status, because a partial or secondary zone reports "active" while
        // Cloudflare is not the authority for it — the failure that looks healthiest.
        if (type is "partial" or "secondary")
        {
            return new DnsZone(
                zone.Id!, name, CanWrite: null, DnsZoneUsability.NotAuthoritative,
                $"{name} only partly uses Cloudflare, so records added here would not take effect. " +
                "Use a different domain, or move this one fully to Cloudflare first.",
                account);
        }

        return status switch
        {
            "active" => new DnsZone(
                zone.Id!, name, CanWrite: null, DnsZoneUsability.Ready,
                $"Ready — DeployAI can point names in {name} at your server.", account),

            "pending" or "initializing" => new DnsZone(
                zone.Id!, name, CanWrite: null, DnsZoneUsability.NotDelegated,
                $"Cloudflare is not in charge of {name} yet — the registrar it was bought from still " +
                "needs pointing at Cloudflare's nameservers. Anything added now will not work until that is done.",
                account),

            _ => new DnsZone(
                zone.Id!, name, CanWrite: null, DnsZoneUsability.Unknown,
                $"{name} is in an unexpected state at Cloudflare" +
                (string.IsNullOrWhiteSpace(status) ? "." : $" (\"{status}\")."), account)
        };
    }

    public async Task<DnsRecordWrite> UpsertAddressRecordAsync(
        ProviderCredentials credentials,
        string zoneId,
        string hostname,
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await FindAddressRecordAsync(credentials, zoneId, hostname, cancellationToken);

            var body = new
            {
                type = "A",
                // The complete name including the zone. A bare label is not expanded.
                name = hostname,
                content = address,
                ttl = RecordTtlSeconds,
                // Never proxied. A proxied record resolves to Cloudflare's own addresses, so the
                // HTTP-01 challenge reaches Cloudflare rather than the server and fails every time.
                proxied = false,
                // Marks the record as ours, so a human looking at the zone knows what wrote it and
                // teardown has something to recognise beyond an id held only in DeployAI's database.
                comment = "Managed by DeployAI"
            };

            if (existing is null)
            {
                using var create = CreateRequest(HttpMethod.Post, credentials, $"zones/{zoneId}/dns_records");
                create.Content = JsonContent.Create(body);
                var created = await SendAsync<CloudflareRecord>(create, cancellationToken);
                return new DnsRecordWrite(created.Value?.Id ?? string.Empty, Created: true);
            }

            using var update = CreateRequest(
                HttpMethod.Put, credentials, $"zones/{zoneId}/dns_records/{existing.Id}");
            update.Content = JsonContent.Create(body);
            var updated = await SendAsync<CloudflareRecord>(update, cancellationToken);
            return new DnsRecordWrite(updated.Value?.Id ?? existing.Id!, Created: false);
        }
        catch (CloudflareApiException ex)
        {
            throw ex.ToDeployAIException($"point {hostname} at {address}");
        }
    }

    public async Task<bool> DeleteRecordAsync(
        ProviderCredentials credentials,
        string zoneId,
        string recordId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Delete, credentials, $"zones/{zoneId}/dns_records/{recordId}");

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);

            // A record that is already gone is the outcome the caller wanted, not a failure.
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the existing A record for a name. <c>name=</c> is an exact match — the
    /// <c>name=contains:</c> and comma-list forms were deprecated in 2025.
    /// </summary>
    private async Task<CloudflareRecord?> FindAddressRecordAsync(
        ProviderCredentials credentials,
        string zoneId,
        string hostname,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get, credentials,
            $"zones/{zoneId}/dns_records?type=A&name={Uri.EscapeDataString(hostname)}");
        var records = await SendAsync<List<CloudflareRecord>>(request, cancellationToken);
        return records.Value?.FirstOrDefault(record => !string.IsNullOrWhiteSpace(record.Id));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, ProviderCredentials credentials, string path)
    {
        var token = credentials.Token?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new DeployAIException(
                "cloudflare_credentials_invalid",
                "Your Cloudflare connection is missing its API token. Reconnect it in settings.");
        }

        var request = new HttpRequestMessage(method, $"{ApiBase}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Sends a request and classifies anything that goes wrong into a verdict.
    /// </summary>
    /// <remarks>
    /// The envelope has to be read rather than the status code trusted: Cloudflare answers 200 with
    /// <c>success: false</c> for some errors, and conversely returns a perfectly successful
    /// envelope with an empty result when a token is simply scoped too narrowly to see anything.
    /// </remarks>
    private async Task<CloudflareResult<T>> SendAsync<T>(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        string body;

        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            throw new CloudflareApiException(
                DnsCredentialVerdict.Unreachable,
                "We could not reach Cloudflare just now, so nothing was checked or changed. Try again in a moment.");
        }

        CloudflareEnvelope<T>? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<CloudflareEnvelope<T>>(body);
        }
        catch (JsonException)
        {
            // Falls through to the status handling below, which says something more useful than a
            // parse error would.
        }

        if (response.IsSuccessStatusCode && envelope is not { Success: false })
        {
            return new CloudflareResult<T>(envelope is null ? default : envelope.Result, envelope?.Info);
        }

        throw Classify(response, envelope?.Errors);
    }

    /// <summary>
    /// Maps Cloudflare's status codes and error codes onto verdicts, with the sentence the user
    /// should read. The codes are the ones a DNS integration actually hits, confirmed against the
    /// live API.
    /// </summary>
    private static CloudflareApiException Classify(
        HttpResponseMessage response, List<CloudflareError>? errors)
    {
        var codes = (errors ?? [])
            .SelectMany(e => new[] { e.Code }.Concat((e.ErrorChain ?? []).Select(c => c.Code)))
            .ToHashSet();
        var detail = string.Join("; ", (errors ?? [])
            .Select(e => e.Message)
            .Where(m => !string.IsNullOrWhiteSpace(m)));

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta
                             ?? (response.Headers.RetryAfter?.Date is { } date
                                 ? date - DateTimeOffset.UtcNow
                                 : null);

            return new CloudflareApiException(
                DnsCredentialVerdict.RateLimited,
                "Cloudflare is limiting requests for this account right now. Nothing was changed — " +
                (retryAfter is { TotalSeconds: > 0 }
                    ? $"try again in about {(int)retryAfter.Value.TotalSeconds} seconds."
                    : "try again shortly."),
                retryAfter);
        }

        // 6003 with a 6111 in the chain means the Authorization header was not even shaped like a
        // token — almost always a copy that got cut short.
        if (codes.Contains(6003) || codes.Contains(6111) || codes.Contains(1001))
        {
            return new CloudflareApiException(
                DnsCredentialVerdict.Malformed,
                "That does not look like a complete Cloudflare API token — it may have been cut short " +
                "when it was copied. Create the token again in Cloudflare and copy the whole value.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || codes.Contains(1000))
        {
            // Deliberately does not say "expired": a token past its expiry returns exactly this
            // response, so the two cannot be told apart, and guessing sends the user looking in the
            // wrong place.
            return new CloudflareApiException(
                DnsCredentialVerdict.Rejected,
                "Cloudflare does not recognise that token. It may have been deleted, replaced, or have " +
                "expired. Create a new one and paste it here.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // The likeliest first-attempt failure by far: Cloudflare's own "Edit zone DNS" template
            // grants DNS Write and no Zone Read, so the token can change records but cannot see
            // which domains exist to change them in.
            var cannotList = detail.Contains("zone.list", StringComparison.OrdinalIgnoreCase) ||
                             codes.Contains(9109);

            return new CloudflareApiException(
                cannotList ? DnsCredentialVerdict.CannotListZones : DnsCredentialVerdict.Rejected,
                cannotList
                    ? "This token can change DNS records but cannot see which domains you own, so " +
                      "DeployAI cannot tell where to put them. In Cloudflare, edit the token so it has " +
                      "both Zone → Zone → Read and Zone → DNS → Edit, and includes the domains you want to use."
                    : "Cloudflare refused that request. The token may not cover the domain you are using — " +
                      "check its Zone Resources in Cloudflare.");
        }

        if ((int)response.StatusCode >= 500)
        {
            return new CloudflareApiException(
                DnsCredentialVerdict.Unreachable,
                "Cloudflare returned an error of its own, so nothing was changed. Try again in a moment.");
        }

        return new CloudflareApiException(
            DnsCredentialVerdict.Rejected,
            string.IsNullOrWhiteSpace(detail)
                ? $"Cloudflare refused that request ({(int)response.StatusCode})."
                : $"Cloudflare refused that request: {detail}");
    }

    /// <summary>
    /// Carries a verdict up from the transport so both the validate path (which reports it) and the
    /// operational paths (which throw) can use one classification.
    /// </summary>
    private sealed class CloudflareApiException : Exception
    {
        public CloudflareApiException(DnsCredentialVerdict verdict, string message, TimeSpan? retryAfter = null)
            : base(message)
        {
            Verdict = verdict;
            RetryAfter = retryAfter;
        }

        public DnsCredentialVerdict Verdict { get; }

        public TimeSpan? RetryAfter { get; }

        /// <summary>
        /// Rate-limited and unreachable get their own error codes so the API edge can answer 429
        /// and 503. Returning them as a plain 400 tells the user their input was wrong when nothing
        /// was checked at all, and invites an immediate retry into a longer lockout.
        /// </summary>
        public DeployAIException ToDeployAIException(string attempted) => Verdict switch
        {
            DnsCredentialVerdict.RateLimited => new DeployAIException(DnsErrorCodes.RateLimited, Message),
            DnsCredentialVerdict.Unreachable => new DeployAIException(DnsErrorCodes.Unreachable, Message),
            _ => new DeployAIException("cloudflare_api_error", $"Could not {attempted}. {Message}")
        };
    }

    private readonly record struct CloudflareResult<T>(T? Value, CloudflareResultInfo? Info);

    private sealed class CloudflareEnvelope<T>
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("result")]
        public T? Result { get; set; }

        [JsonPropertyName("errors")]
        public List<CloudflareError>? Errors { get; set; }

        [JsonPropertyName("result_info")]
        public CloudflareResultInfo? Info { get; set; }
    }

    private sealed class CloudflareResultInfo
    {
        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("total_pages")]
        public int TotalPages { get; set; }

        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }
    }

    private sealed class CloudflareError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>
        /// Present on header/format errors and absent from Cloudflare's documented error schema —
        /// the specific code that says "not shaped like a token" only appears in here.
        /// </summary>
        [JsonPropertyName("error_chain")]
        public List<CloudflareError>? ErrorChain { get; set; }
    }

    private sealed class CloudflareZone
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("account")]
        public CloudflareAccount? Account { get; set; }
    }

    private sealed class CloudflareAccount
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class CloudflareTokenStatus
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("expires_on")]
        public DateTimeOffset? ExpiresOn { get; set; }
    }

    private sealed class CloudflareRecord
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
