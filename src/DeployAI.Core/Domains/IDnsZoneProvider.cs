using System.Text.Json.Serialization;
using DeployAI.Core.Providers;

namespace DeployAI.Core.Domains;

/// <summary>Whether records written into a zone would actually take effect.</summary>
/// <remarks>
/// Serialized by name — this API configures no global enum-as-string converter, and an
/// un-annotated enum crossing the wire as an integer is a bug that has already shipped here once:
/// every client comparison missed and a domain waiting on DNS rendered as "Removed".
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DnsZoneUsability
{
    /// <summary>The zone's state is not one we recognise. Reported, never treated as a failure.</summary>
    Unknown = 0,

    /// <summary>Cloudflare is authoritative and serving. Records written here resolve.</summary>
    Ready = 1,

    /// <summary>
    /// Added to Cloudflare, but the registrar has not pointed the domain at Cloudflare's
    /// nameservers yet. Records can be written and will resolve to nothing.
    /// </summary>
    NotDelegated = 2,

    /// <summary>
    /// A partial (CNAME setup) or secondary zone. The nastiest case: it reports status "active",
    /// so it looks perfectly healthy, while Cloudflare is not the authority for the names in it.
    /// </summary>
    NotAuthoritative = 3,

    /// <summary>Visible to the token, but the token cannot change its records.</summary>
    ReadOnly = 4
}

/// <summary>A zone the connected account can see, and whether it is any use.</summary>
/// <param name="CanWrite">
/// Null when unknown — which is the honest answer before anything has tried to write. Cloudflare's
/// zone payload carries no reliable per-zone write flag under token auth, so guessing one either
/// promises automation that fails or hides automation that would have worked.
/// </param>
/// <param name="UsabilityMessage">
/// Always populated, and written for someone who has never configured DNS: what is wrong and what
/// to do about it.
/// </param>
public sealed record DnsZone(
    string Id,
    string Name,
    bool? CanWrite,
    DnsZoneUsability Usability,
    string UsabilityMessage,
    string? AccountName = null)
{
    public bool IsReady => Usability is DnsZoneUsability.Ready;
}

/// <summary>Why a DNS credential was or was not accepted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DnsCredentialVerdict
{
    /// <summary>Usable: the account's zones could be listed.</summary>
    Ok = 0,

    /// <summary>Not shaped like a token at all — usually a truncated copy.</summary>
    Malformed = 1,

    /// <summary>Well formed, but the provider does not recognise it.</summary>
    Rejected = 2,

    /// <summary>Recognised and past its expiry.</summary>
    Expired = 3,

    /// <summary>Authenticates, but is not allowed to list zones — so nothing can be matched to a domain.</summary>
    CannotListZones = 4,

    /// <summary>Works and is allowed, but the account it belongs to holds no zones.</summary>
    NoZonesVisible = 5,

    /// <summary>The provider is rate-limiting. Says nothing about the credential.</summary>
    RateLimited = 6,

    /// <summary>The provider could not be reached. Says nothing about the credential.</summary>
    Unreachable = 7
}

/// <summary>
/// The outcome of checking a DNS credential, with the message already written.
/// </summary>
/// <remarks>
/// A bool would collapse three different fixes into one sentence: re-copy the token, widen its
/// permissions, or wait and try again. The provider builds the message because it is the only
/// layer that knows what its own error codes mean.
/// </remarks>
public sealed record DnsCredentialCheck(
    DnsCredentialVerdict Verdict,
    string Message,
    IReadOnlyList<DnsZone> Zones,
    DateTimeOffset? TokenExpiresOn = null,
    TimeSpan? RetryAfter = null)
{
    public bool IsUsable => Verdict is DnsCredentialVerdict.Ok;

    /// <summary>
    /// Whether this says something about the credential itself. False for rate-limiting and for an
    /// unreachable provider — those are "could not check", and marking a working connection invalid
    /// because of a network blip is the same mistake as calling a DNS timeout a wrong record.
    /// </summary>
    public bool IsConclusive =>
        Verdict is not (DnsCredentialVerdict.RateLimited or DnsCredentialVerdict.Unreachable);
}

/// <summary>
/// A DNS account DeployAI can read zones from and write records into, so the user never has to
/// leave the product to point a domain at their server.
/// </summary>
/// <remarks>
/// Going to a DNS dashboard to add an A record is exactly the manual step this platform exists to
/// remove: a non-technical user does not know what an A record is, and the guided fallback asks
/// them to type one anyway. This interface is how that step stops being theirs.
/// </remarks>
public interface IDnsZoneProvider
{
    string ProviderName { get; }

    string DisplayName { get; }

    /// <summary>
    /// Checks a credential and, when it works, returns what it can see. Never throws for a
    /// credential problem — every outcome, including "we could not reach the provider", is a
    /// verdict on the result.
    /// </summary>
    Task<DnsCredentialCheck> ValidateCredentialsAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken);

    Task<IReadOnlyList<DnsZone>> ListZonesAsync(
        ProviderCredentials credentials, CancellationToken cancellationToken);

    /// <summary>
    /// Points <paramref name="hostname"/> at <paramref name="address"/> with an A record.
    /// </summary>
    /// <remarks>
    /// Idempotent: an existing A record for the same name is updated rather than joined by a
    /// second one. This matters more than it looks — a provider's duplicate detection keys on
    /// name+type+content, so an update written as a create does not error, it succeeds, and leaves
    /// two A records round-robining half the traffic to an address that no longer answers.
    /// <para>
    /// Always written unproxied. A proxied record resolves to the CDN's own addresses, so the
    /// HTTP-01 challenge reaches the CDN instead of the server and fails every time — and it also
    /// defeats DeployAI's own does-this-reach-my-server check, because the public answer is never
    /// the origin address.
    /// </para>
    /// </remarks>
    Task<DnsRecordWrite> UpsertAddressRecordAsync(
        ProviderCredentials credentials,
        string zoneId,
        string hostname,
        string address,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a record DeployAI created. Called when a domain is detached, so a zone is not left
    /// accumulating records pointing at servers that no longer exist.
    /// </summary>
    Task<bool> DeleteRecordAsync(
        ProviderCredentials credentials,
        string zoneId,
        string recordId,
        CancellationToken cancellationToken);
}

/// <summary>The result of writing a record.</summary>
/// <param name="Created">False when an existing record was updated rather than a new one added.</param>
public sealed record DnsRecordWrite(string RecordId, bool Created);

public interface IDnsZoneProviderFactory
{
    IDnsZoneProvider? GetZoneProvider(string providerName);

    IReadOnlyList<IDnsZoneProvider> All { get; }
}
