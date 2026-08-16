using System.Text.Json;
using DeployAI.Core.Deployments;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

/// <summary>What one pass of the state machine decided, and whether to come back.</summary>
public sealed record DomainTickResult(DomainStatus Status, bool ShouldContinue, string Message);

/// <summary>A domain as the UI sees it.</summary>
public sealed record ProjectDomainView(
    Guid Id,
    Guid DeployTargetId,
    string Hostname,
    string DisplayHostname,
    DomainSource Source,
    DomainStatus Status,
    bool IsPrimary,
    string StatusMessage,
    string? ExpectedAddress,
    DnsRecordInstruction? Instruction,
    string? CertificateIssuer,
    DateTimeOffset? CertificateNotAfter,
    DateTimeOffset? LastCheckedAt);

/// <summary>
/// The record a user has to create when DeployAI cannot write it for them. Always an A record: the
/// proxy routes on the address, and a CNAME resolves fine while still not being routable.
/// </summary>
public sealed record DnsRecordInstruction(string Type, string Name, string Value, string Hint);

public interface IDomainService
{
    Task<ProjectDomainView> AttachAsync(
        Guid userId, Guid projectId, Guid deployTargetId, string typedDomain, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectDomainView>> ListAsync(Guid userId, Guid projectId, CancellationToken cancellationToken);

    /// <summary>The zones the user's connected DNS accounts can write records into.</summary>
    Task<IReadOnlyList<DnsZone>> ListConnectedZonesAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// A free name under DeployAI's own zone that the project could use, or null when no platform
    /// zone is configured. Needs no DNS work from the user at all.
    /// </summary>
    Task<string?> SuggestPlatformSubdomainAsync(Guid userId, Guid projectId, CancellationToken cancellationToken);

    Task<ProjectDomainView> RetryAsync(Guid userId, Guid domainId, CancellationToken cancellationToken);

    Task RemoveAsync(Guid userId, Guid domainId, CancellationToken cancellationToken);

    /// <summary>Advances one domain by exactly one step. Called only by the reconciliation job.</summary>
    Task<DomainTickResult> ReconcileOnceAsync(Guid domainId, CancellationToken cancellationToken);
}

public sealed class DomainService : IDomainService
{
    private readonly DeployAIDbContext _db;
    private readonly IDnsResolver _dns;
    private readonly ICertificateInspector _certificates;
    private readonly IServerAddressProviderFactory _serverAddresses;
    private readonly IApplicationDomainAssignmentFactory _domainAssignments;
    private readonly IDnsZoneProviderFactory _dnsZones;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IDeploymentOrchestrator _orchestrator;
    private readonly IDomainReconciliationScheduler _scheduler;
    private readonly IOptions<AppOptions> _app;
    private readonly TimeProvider _clock;
    private readonly ILogger<DomainService> _logger;

    public DomainService(
        DeployAIDbContext db,
        IDnsResolver dns,
        ICertificateInspector certificates,
        IServerAddressProviderFactory serverAddresses,
        IApplicationDomainAssignmentFactory domainAssignments,
        IDnsZoneProviderFactory dnsZones,
        IProviderCredentialTokenService tokens,
        IDeploymentOrchestrator orchestrator,
        IDomainReconciliationScheduler scheduler,
        IOptions<AppOptions> app,
        TimeProvider clock,
        ILogger<DomainService> logger)
    {
        _db = db;
        _dns = dns;
        _certificates = certificates;
        _serverAddresses = serverAddresses;
        _domainAssignments = domainAssignments;
        _dnsZones = dnsZones;
        _tokens = tokens;
        _orchestrator = orchestrator;
        _scheduler = scheduler;
        _app = app;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ProjectDomainView> AttachAsync(
        Guid userId,
        Guid projectId,
        Guid deployTargetId,
        string typedDomain,
        CancellationToken cancellationToken)
    {
        if (!DomainNameRules.TryNormalize(typedDomain, out var hostname, out var rejection))
        {
            throw new DeployAIException("domain_invalid", rejection!.Reason);
        }

        var target = await _db.DeployTargets
            .Include(t => t.Project)
            .FirstOrDefaultAsync(
                t => t.Id == deployTargetId && t.ProjectId == projectId && t.Project.UserId == userId,
                cancellationToken)
            ?? throw new DeployAIException("deploy_target_not_found", "That part of the project no longer exists.");

        var existing = await _db.ProjectDomains.FirstOrDefaultAsync(
            d => d.DeployTargetId == deployTargetId && d.Hostname == hostname, cancellationToken);
        if (existing is not null)
        {
            return await ViewOf(existing, cancellationToken);
        }

        var now = _clock.GetUtcNow();
        var domain = new ProjectDomain
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            DeployTargetId = deployTargetId,
            Hostname = hostname,
            DisplayHostname = typedDomain.Trim(),
            // A name under DeployAI's own zone is already covered by its wildcard record, so the
            // user is never shown a record to create for it. It still goes through the same DNS
            // check as anything else — it will simply pass on the first attempt, and a special
            // path that skipped the check would be a path around the certificate gate.
            Source = IsPlatformSubdomain(hostname) ? DomainSource.PlatformSubdomain : DomainSource.UserProvided,
            Status = DomainStatus.Pending,
            IsPrimary = !await _db.ProjectDomains.AnyAsync(d => d.ProjectId == projectId, cancellationToken),
            StatusMessage = "Working out where this domain needs to point.",
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.ProjectDomains.Add(domain);
        await _db.SaveChangesAsync(cancellationToken);

        _scheduler.Start(domain.Id);
        return await ViewOf(domain, cancellationToken);
    }

    public async Task<string?> SuggestPlatformSubdomainAsync(
        Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var taken = await _db.ProjectDomains
            .Select(d => d.Hostname)
            .ToListAsync(cancellationToken);

        return PlatformSubdomain.TryBuild(
            project.Name, _app.Value.PlatformDomain, taken.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The zones the user's connected DNS accounts can write to, so the wizard can offer to point
    /// the domain itself instead of handing over a record to copy.
    /// </summary>
    public async Task<IReadOnlyList<DnsZone>> ListConnectedZonesAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var credentials = await _db.ProviderCredentials
            .Where(c => c.UserId == userId && c.Kind == CredentialKind.Dns)
            .ToListAsync(cancellationToken);

        var zones = new List<DnsZone>();
        foreach (var credential in credentials)
        {
            var provider = _dnsZones.GetZoneProvider(credential.ProviderName);
            if (provider is null)
            {
                continue;
            }

            try
            {
                var token = new ProviderCredentials(await _tokens.GetTokenAsync(credential, cancellationToken));
                zones.AddRange(await provider.ListZonesAsync(token, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable account must not hide the zones of the others.
                _logger.LogWarning(
                    ex, "Could not list zones from the {Provider} connection.", credential.ProviderName);
            }
        }

        return zones;
    }

    /// <summary>
    /// Writes the A record for a domain into a connected zone, so the user never opens a DNS
    /// dashboard.
    /// </summary>
    /// <remarks>
    /// Called during attach when a connected zone covers the hostname. Failing to write is not
    /// fatal — the domain falls back to the guided instructions and keeps waiting, which is what it
    /// would have done anyway if no account had been connected.
    /// </remarks>
    private async Task TryWriteRecordAsync(
        ProjectDomain domain, Guid userId, string address, CancellationToken cancellationToken)
    {
        var credentials = await _db.ProviderCredentials
            .Where(c => c.UserId == userId && c.Kind == CredentialKind.Dns)
            .ToListAsync(cancellationToken);

        foreach (var credential in credentials)
        {
            var provider = _dnsZones.GetZoneProvider(credential.ProviderName);
            if (provider is null)
            {
                continue;
            }

            try
            {
                var token = new ProviderCredentials(await _tokens.GetTokenAsync(credential, cancellationToken));
                var zones = await provider.ListZonesAsync(token, cancellationToken);

                // Only a zone Cloudflare is actually authoritative for and serving. A zone whose
                // registrar has not delegated yet, or one set up as partial, accepts the write
                // happily and then resolves to nothing — leaving the domain to wait out its
                // deadline and be reported as the user's mistake.
                // The longest matching zone wins: an account holding both example.com and
                // eu.example.com should write app.eu.example.com into the more specific one.
                var zone = zones
                    .Where(z => z.IsReady && CoversHostname(z.Name, domain.Hostname))
                    .OrderByDescending(z => z.Name.Length)
                    .FirstOrDefault();

                if (zone is null)
                {
                    continue;
                }

                var written = await provider.UpsertAddressRecordAsync(
                    token, zone.Id, domain.Hostname, address, cancellationToken);

                domain.Source = DomainSource.ManagedZone;
                domain.DnsCredentialId = credential.Id;
                domain.ZoneId = zone.Id;
                domain.ManagedRecordId = written.RecordId;
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex, "Could not write the DNS record for {Hostname} through {Provider}.",
                    domain.Hostname, credential.ProviderName);

                // A token that has been revoked or has expired otherwise fails silently forever:
                // the write is best-effort, the domain quietly falls back to guided instructions,
                // and nothing anywhere connects the two. Marking the connection is what lets the
                // user be told which account stopped working, rather than left to notice that
                // their DNS stopped being automatic.
                MarkInvalidIfConclusive(credential, ex);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records that a connection is broken — but only when the failure actually says so. Rate
    /// limiting and an unreachable provider are "could not check", and condemning a working
    /// connection over a network blip is the same mistake as calling a DNS timeout a wrong record.
    /// </summary>
    private static void MarkInvalidIfConclusive(ProviderCredential credential, Exception failure)
    {
        if (failure is not DeployAIException deployAiFailure ||
            deployAiFailure.ErrorCode is DnsErrorCodes.RateLimited or DnsErrorCodes.Unreachable)
        {
            return;
        }

        credential.IsValid = false;
        credential.LastValidatedAt = DateTimeOffset.UtcNow;
    }

    private bool IsPlatformSubdomain(string hostname) =>
        !string.IsNullOrWhiteSpace(_app.Value.PlatformDomain) &&
        CoversHostname(_app.Value.PlatformDomain.Trim().Trim('.'), hostname);

    private static bool CoversHostname(string zone, string hostname) =>
        string.Equals(zone, hostname, StringComparison.OrdinalIgnoreCase) ||
        hostname.EndsWith($".{zone}", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ProjectDomainView>> ListAsync(
        Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        var domains = await _db.ProjectDomains
            .Include(d => d.Project)
            .Where(d => d.ProjectId == projectId && d.Project.UserId == userId)
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.Hostname)
            .ToListAsync(cancellationToken);

        var views = new List<ProjectDomainView>(domains.Count);
        foreach (var domain in domains)
        {
            views.Add(await ViewOf(domain, cancellationToken));
        }

        return views;
    }

    public async Task<ProjectDomainView> RetryAsync(Guid userId, Guid domainId, CancellationToken cancellationToken)
    {
        var domain = await RequireOwnedAsync(userId, domainId, cancellationToken);

        // A retry is a fresh start, not a continuation: the clock, the attempt counter and the
        // record of what previously concluded all reset, or the first tick would land straight back
        // on the old deadline.
        domain.Status = DomainStatus.Pending;
        domain.AttemptCount = 0;
        domain.DeadlineAt = null;
        domain.LastConclusiveStatus = null;
        domain.RoutingDeployTriggeredForAttempt = null;
        domain.StatusMessage = "Checking this domain again.";
        domain.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(cancellationToken);

        _scheduler.Start(domain.Id);
        return await ViewOf(domain, cancellationToken);
    }

    public async Task RemoveAsync(Guid userId, Guid domainId, CancellationToken cancellationToken)
    {
        var domain = await RequireOwnedAsync(userId, domainId, cancellationToken);

        // Release what DeployAI wrote. A record left behind points at a server that is about to
        // stop answering for this domain, and it is in the user's own zone where they would have
        // to find and delete it themselves — which is the manual step this feature removes.
        if (domain is { DnsCredentialId: { } credentialId, ZoneId: { } zoneId, ManagedRecordId: { } recordId })
        {
            var credential = await _db.ProviderCredentials
                .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId, cancellationToken);
            var provider = credential is null ? null : _dnsZones.GetZoneProvider(credential.ProviderName);

            if (credential is not null && provider is not null)
            {
                try
                {
                    var token = new ProviderCredentials(
                        await _tokens.GetTokenAsync(credential, cancellationToken));
                    await provider.DeleteRecordAsync(token, zoneId, recordId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Removing the domain from the project is what the user asked for; a record we
                    // could not clean up must not block it.
                    _logger.LogWarning(
                        ex, "Could not release the DNS record for {Hostname}.", domain.Hostname);
                }
            }
        }

        _db.ProjectDomains.Remove(domain);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DomainTickResult> ReconcileOnceAsync(Guid domainId, CancellationToken cancellationToken)
    {
        var domain = await _db.ProjectDomains
            .Include(d => d.DeployTarget).ThenInclude(t => t.Credential)
            .Include(d => d.Project)
            .FirstOrDefaultAsync(d => d.Id == domainId, cancellationToken);

        if (domain is null)
        {
            return new DomainTickResult(DomainStatus.Retired, false, "This domain no longer exists.");
        }

        if (DomainReconciliation.IsTerminal(domain.Status))
        {
            return new DomainTickResult(domain.Status, false, domain.StatusMessage);
        }

        var now = _clock.GetUtcNow();
        var deadlinePassed = domain.DeadlineAt is { } deadline && now > deadline;

        try
        {
            var result = domain.Status switch
            {
                DomainStatus.Pending => await ResolveTargetAddressAsync(domain, cancellationToken),
                DomainStatus.DnsPending => await CheckDnsAsync(domain, deadlinePassed, cancellationToken),
                DomainStatus.DnsVerified => await AssignAsync(domain, cancellationToken),
                DomainStatus.Assigned => await TriggerRoutingDeployAsync(domain, cancellationToken),
                DomainStatus.CertificatePending => await CheckCertificateAsync(domain, deadlinePassed, cancellationToken),
                _ => new DomainTickResult(domain.Status, false, domain.StatusMessage)
            };

            domain.LastCheckedAt = now;
            domain.UpdatedAt = now;
            domain.AttemptCount++;
            await _db.SaveChangesAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A step that threw is a step that did not happen, which is not the same as a domain
            // that failed. Keep the state, record why, and let the deadline decide.
            _logger.LogWarning(ex, "Reconciling domain {Hostname} did not complete this pass.", domain.Hostname);
            domain.LastCheckedAt = now;
            domain.UpdatedAt = now;
            domain.AttemptCount++;
            await _db.SaveChangesAsync(cancellationToken);
            return new DomainTickResult(domain.Status, !deadlinePassed, domain.StatusMessage);
        }
    }

    private async Task<DomainTickResult> ResolveTargetAddressAsync(
        ProjectDomain domain, CancellationToken cancellationToken)
    {
        var target = domain.DeployTarget;
        var addresses = _serverAddresses.GetServerAddressProvider(target.ProviderName);
        if (addresses is null)
        {
            return Terminal(domain, DomainStatus.DnsUnverifiable,
                $"{target.ProviderName} does not expose a server address to point a domain at.");
        }

        var credentials = new ProviderCredentials(
            await _tokens.GetTokenAsync(target.Credential, cancellationToken));
        var address = await addresses.TryGetServerAddressAsync(credentials, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(address))
        {
            // Guessing here would send someone to point their domain at a machine their app is not
            // on, and the resulting failure would look like their mistake.
            return Terminal(domain, DomainStatus.DnsUnverifiable,
                "Could not work out which server address this domain should point at, so no DNS " +
                "record can be suggested yet.");
        }

        domain.ExpectedAddress = address;

        // Written here rather than at attach because this is the first point the address is known.
        // If a connected account owns the zone the user never touches DNS at all; if not, this is a
        // no-op and the guided instructions carry them instead.
        await TryWriteRecordAsync(domain, domain.Project.UserId, address, cancellationToken);

        domain.Status = DomainStatus.DnsPending;
        // A record DeployAI wrote itself should appear within its own short TTL, so waiting an hour
        // for one would mean waiting fifty minutes past the point it became a provider problem.
        domain.DeadlineAt = _clock.GetUtcNow().Add(domain.Source == DomainSource.ManagedZone
            ? DomainReconciliation.ManagedDnsDeadline
            : DomainReconciliation.DnsDeadline);
        domain.StatusMessage = domain.Source == DomainSource.ManagedZone
            ? $"Pointed {domain.Hostname} at {address}. Waiting for it to take effect."
            : $"Waiting for {domain.Hostname} to point at {address}.";
        return new DomainTickResult(domain.Status, true, domain.StatusMessage);
    }

    private async Task<DomainTickResult> CheckDnsAsync(
        ProjectDomain domain, bool deadlinePassed, CancellationToken cancellationToken)
    {
        var check = await _dns.CheckAsync(domain.Hostname, domain.ExpectedAddress!, cancellationToken);
        var transition = DomainTransitions.AfterDnsCheck(
            check, deadlinePassed, ToLifecycle(domain.LastConclusiveStatus));

        domain.ObservationsJson = JsonSerializer.Serialize(check);
        return Apply(domain, transition);
    }

    private async Task<DomainTickResult> AssignAsync(ProjectDomain domain, CancellationToken cancellationToken)
    {
        // The gate. Nothing reaches an https:// write without passing through here, so no caller
        // can start a certificate challenge against a domain that has not been proven to resolve.
        if (!DomainTransitions.MayRequestCertificate(ToLifecycle(domain.Status)!.Value))
        {
            return new DomainTickResult(domain.Status, true, domain.StatusMessage);
        }

        var target = domain.DeployTarget;
        var assignment = _domainAssignments.GetDomainAssignment(target.ProviderName);
        if (assignment is null || string.IsNullOrWhiteSpace(target.ProviderProjectId))
        {
            return Terminal(domain, DomainStatus.DnsUnverifiable,
                $"{target.ProviderName} cannot be given a domain through DeployAI yet.");
        }

        var config = DeployTargetConfig.Parse(target.ConfigJson);
        var credentials = new ProviderCredentials(
            await _tokens.GetTokenAsync(target.Credential, cancellationToken));
        var fqdn = $"https://{domain.Hostname}";

        try
        {
            await assignment.AssignApplicationDomainAsync(
                credentials, target.ProviderProjectId, fqdn, config.DomainServiceName,
                config.IsComposeTarget, cancellationToken);
        }
        catch (DeployAIException ex) when (ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase))
        {
            return Terminal(domain, DomainStatus.Conflicted,
                $"{domain.Hostname} is already attached to something else on this server.");
        }

        var readBack = await assignment.ReadAssignedDomainsAsync(
            credentials, target.ProviderProjectId, config.IsComposeTarget, cancellationToken);

        // A read that could not happen is not a read that found nothing. Only an affirmative
        // "the domain is absent" is worth retrying on; anything else falls through to the
        // certificate check, which is the real proof either way.
        if (readBack.CouldRead && !readBack.Includes(domain.Hostname))
        {
            domain.StatusMessage =
                $"{domain.Hostname} was accepted but did not stick. Trying again.";
            return new DomainTickResult(domain.Status, true, domain.StatusMessage);
        }

        domain.Status = DomainStatus.Assigned;
        domain.StatusMessage = $"{domain.Hostname} is attached. Restarting the app so it takes effect.";
        return new DomainTickResult(domain.Status, true, domain.StatusMessage);
    }

    private async Task<DomainTickResult> TriggerRoutingDeployAsync(
        ProjectDomain domain, CancellationToken cancellationToken)
    {
        // The proxy regenerates its routes at deploy time, so the deploy that attaches a domain is
        // never the one that serves it. Guarded by the attempt it fired for, because the deploy
        // this triggers runs the post-deploy assignment again, which would otherwise wake this
        // reconciler into triggering another deploy, forever.
        if (domain.RoutingDeployTriggeredForAttempt is null)
        {
            domain.RoutingDeployTriggeredForAttempt = domain.AttemptCount;

            await _orchestrator.TriggerTargetAsync(
                domain.ProjectId,
                domain.Project.UserId,
                domain.DeployTargetId,
                domain.Project.DefaultBranch,
                cancellationToken);
        }

        domain.Status = DomainStatus.CertificatePending;
        domain.DeadlineAt = _clock.GetUtcNow().Add(DomainReconciliation.CertificateDeadline);
        domain.StatusMessage = $"Waiting for the certificate for {domain.Hostname}.";
        return new DomainTickResult(domain.Status, true, domain.StatusMessage);
    }

    private async Task<DomainTickResult> CheckCertificateAsync(
        ProjectDomain domain, bool deadlinePassed, CancellationToken cancellationToken)
    {
        var inspection = await _certificates.InspectAsync(domain.Hostname, cancellationToken);
        var transition = DomainTransitions.AfterCertificateCheck(inspection, deadlinePassed);

        domain.CertificateIssuer = inspection.Issuer;
        domain.CertificateNotAfter = inspection.NotAfter;
        domain.ObservationsJson = JsonSerializer.Serialize(inspection);
        return Apply(domain, transition);
    }

    private static DomainTickResult Apply(ProjectDomain domain, DomainTransition transition)
    {
        domain.Status = (DomainStatus)transition.State;
        domain.StatusMessage = transition.Message;
        if (transition.ConclusiveStatus is { } conclusive)
        {
            domain.LastConclusiveStatus = (DomainStatus)conclusive;
        }

        return new DomainTickResult(domain.Status, !transition.IsTerminal, transition.Message);
    }

    private static DomainTickResult Terminal(ProjectDomain domain, DomainStatus status, string message)
    {
        domain.Status = status;
        domain.StatusMessage = message;
        return new DomainTickResult(status, false, message);
    }

    private static DomainLifecycleState? ToLifecycle(DomainStatus? status) =>
        status is null ? null : (DomainLifecycleState)status.Value;

    private async Task<ProjectDomain> RequireOwnedAsync(
        Guid userId, Guid domainId, CancellationToken cancellationToken) =>
        await _db.ProjectDomains
            .Include(d => d.Project)
            .FirstOrDefaultAsync(d => d.Id == domainId && d.Project.UserId == userId, cancellationToken)
        ?? throw new DeployAIException("domain_not_found", "That domain is not on any of your projects.");

    private Task<ProjectDomainView> ViewOf(ProjectDomain domain, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        DnsRecordInstruction? instruction = domain.ExpectedAddress is { Length: > 0 } address &&
                                            domain.Source is not DomainSource.PlatformSubdomain
            ? new DnsRecordInstruction(
                "A",
                domain.Hostname,
                address,
                "Create this as an A record. If your DNS provider offers a proxy or CDN toggle, " +
                "leave it off until the certificate has been issued.")
            : null;

        return Task.FromResult(new ProjectDomainView(
            domain.Id,
            domain.DeployTargetId,
            domain.Hostname,
            domain.DisplayHostname,
            domain.Source,
            domain.Status,
            domain.IsPrimary,
            domain.StatusMessage,
            domain.ExpectedAddress,
            instruction,
            domain.CertificateIssuer,
            domain.CertificateNotAfter,
            domain.LastCheckedAt));
    }
}
