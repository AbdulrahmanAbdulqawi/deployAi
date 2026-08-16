using DeployAI.Api.Services;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Controllers;

/// <summary>
/// DNS accounts DeployAI writes records into, so pointing a domain at a server stops being
/// something the user does in someone else's dashboard.
/// </summary>
/// <remarks>
/// Separate from <c>CredentialsController</c> for the same reason <c>StorageController</c> is:
/// that controller resolves every provider name through <c>IProviderFactory</c>, which knows only
/// deployment providers and throws for anything else. DNS connections are stored with
/// <see cref="CredentialKind.Dns"/> so they never appear in a deploy-target picker.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/dns")]
public sealed class DnsController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IDnsZoneProviderFactory _zoneProviders;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<DnsController> _logger;

    public DnsController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IDnsZoneProviderFactory zoneProviders,
        IProviderCredentialTokenService tokens,
        IEncryptionService encryption,
        ILogger<DnsController> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _zoneProviders = zoneProviders;
        _tokens = tokens;
        _encryption = encryption;
        _logger = logger;
    }

    /// <summary>The DNS providers a token can be pasted for.</summary>
    [HttpGet("providers")]
    public IActionResult ListProviders() =>
        Ok(new
        {
            providers = _zoneProviders.All
                .Select(p => new { name = p.ProviderName, displayName = p.DisplayName })
        });

    [HttpGet("connections")]
    public async Task<IActionResult> ListConnections(CancellationToken cancellationToken)
    {
        var stored = await LoadConnectionsAsync(RequireUserId(), cancellationToken);
        return Ok(new { connections = stored.Select(ToSummary).ToList() });
    }

    /// <summary>
    /// Checks a token and, if it works, saves it. Nothing is persisted until the provider confirms
    /// it can actually see the account's domains.
    /// </summary>
    [HttpPost("connections")]
    public async Task<IActionResult> CreateConnection(
        [FromBody] CreateDnsConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var providerName = string.IsNullOrWhiteSpace(request.ProviderName)
            ? "cloudflare"
            : request.ProviderName.Trim();

        var provider = _zoneProviders.GetZoneProvider(providerName)
            ?? throw new DeployAIException(
                "dns_provider_unknown", $"'{providerName}' is not a DNS provider DeployAI supports.");

        var token = request.Token?.Trim() ?? string.Empty;
        var check = await provider.ValidateCredentialsAsync(
            new ProviderCredentials(token), cancellationToken);

        if (!check.IsUsable)
        {
            // Rate-limiting and an unreachable provider say nothing about the token, so they get
            // their own codes (429/503 at the edge) and never mark anything invalid.
            throw new DeployAIException(VerdictToErrorCode(check.Verdict), check.Message);
        }

        var label = string.IsNullOrWhiteSpace(request.Label) ? "Default" : request.Label.Trim();

        // Dedupe on the shape the unique index actually uses — (UserId, ProviderName, Label), which
        // does not include Kind — or a repeat connect surfaces a raw unique violation.
        var existing = await _db.ProviderCredentials.FirstOrDefaultAsync(
            c => c.UserId == userId && c.ProviderName == providerName && c.Label == label,
            cancellationToken);

        if (existing is not null && existing.Kind != CredentialKind.Dns)
        {
            throw new DeployAIException(
                "dns_label_in_use",
                $"You already have a different kind of connection called \"{label}\". Give this one another name.");
        }

        if (existing is not null)
        {
            existing.TokenEncrypted = _encryption.Encrypt(token);
            existing.IsValid = true;
            existing.LastValidatedAt = DateTimeOffset.UtcNow;
            existing.ExpiresAt = check.TokenExpiresOn;
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(ToDetail(existing, check.Zones));
        }

        var credential = new ProviderCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProviderName = providerName,
            Kind = CredentialKind.Dns,
            Label = label,
            TokenEncrypted = _encryption.Encrypt(token),
            IsValid = true,
            LastValidatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = check.TokenExpiresOn,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.ProviderCredentials.Add(credential);
        await _db.SaveChangesAsync(cancellationToken);

        return Created($"/api/dns/connections/{credential.Id}", ToDetail(credential, check.Zones));
    }

    /// <summary>
    /// The zones this connection can see, re-read from the provider every time.
    /// </summary>
    /// <remarks>
    /// Never cached on purpose: a zone that was waiting on its registrar when the account was
    /// connected becomes usable an hour later when the delegation lands, and a cached list would
    /// keep telling the user it is not ready long after it is.
    /// </remarks>
    [HttpGet("connections/{id:guid}/zones")]
    public async Task<IActionResult> ListZones(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var (credential, provider) = await RequireConnectionAsync(userId, id, cancellationToken);
        var token = new ProviderCredentials(await _tokens.GetTokenAsync(credential, cancellationToken));

        var check = await provider.ValidateCredentialsAsync(token, cancellationToken);

        // Only a conclusive answer may change the stored verdict. A rate-limit or a network blip
        // must not mark a working connection broken.
        if (check.IsConclusive && credential.IsValid != check.IsUsable)
        {
            credential.IsValid = check.IsUsable;
            credential.LastValidatedAt = DateTimeOffset.UtcNow;
            credential.ExpiresAt = check.TokenExpiresOn ?? credential.ExpiresAt;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(new { zones = check.Zones, connection = ToSummary(credential), message = check.Message });
    }

    /// <summary>
    /// What disconnecting would do, in plain words, before anything is done.
    /// </summary>
    [HttpGet("connections/{id:guid}/impact")]
    public async Task<IActionResult> PreviewDisconnect(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        await RequireConnectionAsync(userId, id, cancellationToken);

        var dependents = await LoadDependentsAsync(id, cancellationToken);
        var handedOver = dependents.Where(d => HasLiveDnsRecord(d.Status)).Select(d => d.Hostname).ToList();
        var released = dependents.Where(d => !HasLiveDnsRecord(d.Status)).Select(d => d.Hostname).ToList();

        return Ok(new DisconnectImpact(
            dependents.Count,
            handedOver,
            released,
            Describe(handedOver, released)));
    }

    /// <summary>
    /// Removes the connection, releasing the records DeployAI wrote for domains that are not yet
    /// live and handing the live ones back to the user.
    /// </summary>
    /// <remarks>
    /// It would be simpler to refuse while any domain depends on the connection, the way a
    /// deployment credential does — but that comparison does not hold. An app cannot run without
    /// its deploy credential; a DNS record that has already been written keeps resolving perfectly
    /// once the credential is gone. Blocking would tell someone they may never disconnect
    /// Cloudflare because they once used it.
    /// <para>
    /// And it would be actively wrong to delete every record: those are pointing working sites at a
    /// running server, so removing them takes those sites down as a side effect of a settings
    /// action nobody expected to be destructive.
    /// </para>
    /// </remarks>
    [HttpDelete("connections/{id:guid}")]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var (credential, provider) = await RequireConnectionAsync(userId, id, cancellationToken);
        var dependents = await LoadDependentsAsync(id, cancellationToken);

        // Released before the credential goes, because releasing needs the token.
        foreach (var domain in dependents.Where(d => !HasLiveDnsRecord(d.Status)))
        {
            await TryReleaseAsync(provider, credential, domain, cancellationToken);
        }

        foreach (var domain in dependents)
        {
            // Handed back: the record stays exactly as it is, and the domain stops claiming
            // DeployAI manages it. ViewOf already renders the A record for a user-provided domain,
            // so the panel shows them what they now own without any new UI.
            domain.Source = DomainSource.UserProvided;
            domain.DnsCredentialId = null;
            domain.ZoneId = null;
            domain.ManagedRecordId = null;
            domain.UpdatedAt = DateTimeOffset.UtcNow;

            if (HasLiveDnsRecord(domain.Status))
            {
                domain.StatusMessage =
                    $"{domain.Hostname} is still working. Its DNS record is yours to maintain now that " +
                    "the Cloudflare connection has been removed.";
            }
        }

        _db.ProviderCredentials.Remove(credential);
        await _db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private async Task TryReleaseAsync(
        IDnsZoneProvider provider,
        ProviderCredential credential,
        ProjectDomain domain,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domain.ZoneId) || string.IsNullOrWhiteSpace(domain.ManagedRecordId))
        {
            return;
        }

        try
        {
            var token = new ProviderCredentials(await _tokens.GetTokenAsync(credential, cancellationToken));
            await provider.DeleteRecordAsync(
                token, domain.ZoneId, domain.ManagedRecordId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Disconnecting is what the user asked for; a record we could not tidy up must not
            // block it.
            _logger.LogWarning(
                ex, "Could not release the DNS record for {Hostname} while disconnecting.", domain.Hostname);
        }
    }

    /// <summary>
    /// Whether this domain's DNS record is doing real work — either already serving, or attached to
    /// the app and about to. Deleting one of these would break something the user can see.
    /// </summary>
    private static bool HasLiveDnsRecord(DomainStatus status) =>
        status is DomainStatus.DnsVerified
            or DomainStatus.Assigned
            or DomainStatus.CertificatePending
            or DomainStatus.Active;

    private static string Describe(IReadOnlyList<string> handedOver, IReadOnlyList<string> released)
    {
        if (handedOver.Count == 0 && released.Count == 0)
        {
            return "Nothing else is using this connection, so removing it changes nothing.";
        }

        var parts = new List<string>();

        if (handedOver.Count > 0)
        {
            parts.Add(
                $"{Join(handedOver)} will keep working exactly as {(handedOver.Count == 1 ? "it does" : "they do")} " +
                "now, but you'll manage the DNS yourself from here on.");
        }

        if (released.Count > 0)
        {
            parts.Add(
                $"{Join(released)} {(released.Count == 1 ? "is" : "are")} still being set up, so the " +
                $"record{(released.Count == 1 ? "" : "s")} DeployAI added will be tidied up.");
        }

        return string.Join(" ", parts);
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{names[0]} and {names.Count - 1} others"
    };

    private async Task<List<ProjectDomain>> LoadDependentsAsync(Guid credentialId, CancellationToken cancellationToken) =>
        // There is no foreign key on DnsCredentialId, so nothing does this automatically and
        // nothing would stop the rows becoming orphans pointing at a credential that is gone.
        await _db.ProjectDomains
            .Where(d => d.DnsCredentialId == credentialId)
            .ToListAsync(cancellationToken);

    private async Task<List<ProviderCredential>> LoadConnectionsAsync(
        Guid userId, CancellationToken cancellationToken) =>
        await _db.ProviderCredentials
            .Where(c => c.UserId == userId && c.Kind == CredentialKind.Dns)
            .OrderBy(c => c.Label)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Loads a connection the caller owns.
    /// </summary>
    /// <remarks>
    /// The <see cref="CredentialKind.Dns"/> filter is a security boundary rather than tidiness:
    /// without it these routes would read and delete deployment credentials by id, bypassing the
    /// in-use guard that stops a credential being removed out from under a running app.
    /// </remarks>
    private async Task<(ProviderCredential Credential, IDnsZoneProvider Provider)> RequireConnectionAsync(
        Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var credential = await _db.ProviderCredentials.FirstOrDefaultAsync(
            c => c.Id == id && c.UserId == userId && c.Kind == CredentialKind.Dns, cancellationToken)
            ?? throw new DeployAIException("not_found", "We couldn't find that DNS connection.");

        var provider = _zoneProviders.GetZoneProvider(credential.ProviderName)
            ?? throw new DeployAIException(
                "dns_provider_unknown",
                $"'{credential.ProviderName}' is no longer a DNS provider DeployAI supports.");

        return (credential, provider);
    }

    private static string VerdictToErrorCode(DnsCredentialVerdict verdict) => verdict switch
    {
        DnsCredentialVerdict.Malformed => "dns_token_malformed",
        DnsCredentialVerdict.Rejected => "dns_token_rejected",
        DnsCredentialVerdict.Expired => "dns_token_expired",
        DnsCredentialVerdict.CannotListZones => "dns_token_cannot_list_zones",
        DnsCredentialVerdict.NoZonesVisible => "dns_no_zones_visible",
        DnsCredentialVerdict.RateLimited => "cloudflare_rate_limited",
        DnsCredentialVerdict.Unreachable => "cloudflare_unreachable",
        _ => "dns_token_rejected"
    };

    private DnsConnectionSummary ToSummary(ProviderCredential credential)
    {
        var provider = _zoneProviders.GetZoneProvider(credential.ProviderName);
        return new DnsConnectionSummary(
            credential.Id,
            credential.ProviderName,
            provider?.DisplayName ?? credential.ProviderName,
            credential.Label,
            credential.IsValid,
            credential.LastValidatedAt,
            credential.ExpiresAt);
    }

    private DnsConnectionDetail ToDetail(ProviderCredential credential, IReadOnlyList<DnsZone> zones) =>
        new(ToSummary(credential), zones);

    private Guid RequireUserId() =>
        _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");

    public sealed record CreateDnsConnectionRequest(string Token, string? ProviderName = null, string? Label = null);

    /// <summary>
    /// A stored connection. Never carries the token or any part of it — a secret in a response
    /// reaches logs, and Cloudflare shows a token's value exactly once.
    /// </summary>
    public sealed record DnsConnectionSummary(
        Guid Id,
        string ProviderName,
        string DisplayName,
        string Label,
        bool IsValid,
        DateTimeOffset? LastValidatedAt,
        DateTimeOffset? ExpiresAt);

    public sealed record DnsConnectionDetail(DnsConnectionSummary Connection, IReadOnlyList<DnsZone> Zones);

    public sealed record DisconnectImpact(
        int DependentCount,
        IReadOnlyList<string> KeepWorking,
        IReadOnlyList<string> WillBeReleased,
        string Summary);
}
