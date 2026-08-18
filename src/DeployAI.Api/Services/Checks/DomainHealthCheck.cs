using DeployAI.Core.Deployments;
using DeployAI.Core.Domains;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services.Checks;

/// <summary>
/// Re-checks the domains that are already live.
/// </summary>
/// <remarks>
/// <para>
/// Domains were reconciled hard until they went <see cref="DomainStatus.Active"/> and then never
/// looked at again, so a certificate that failed to renew or a DNS record someone deleted went
/// unnoticed until a user hit the site and got a browser warning. Everything needed to notice was
/// already built — the resolver and the certificate inspector — and simply stopped being called.
/// </para>
/// <para>
/// Only <see cref="DomainStatus.Active"/> domains. Everything earlier in the lifecycle belongs to
/// <c>DomainReconciliationJob</c>, which is driving it towards active on its own backoff ladder; two
/// loops advancing one row would race each other through the state machine.
/// </para>
/// </remarks>
public sealed class DomainHealthCheck : IProjectCheckContributor
{
    private readonly DeployAIDbContext _db;
    private readonly ICertificateInspector _certificates;
    private readonly IDnsResolver _dns;
    private readonly FleetVerificationOptions _options;

    public DomainHealthCheck(
        DeployAIDbContext db,
        ICertificateInspector certificates,
        IDnsResolver dns,
        IOptions<FleetVerificationOptions> options)
    {
        _db = db;
        _certificates = certificates;
        _dns = dns;
        _options = options.Value;
    }

    public string Name => "domains";

    public async Task<IReadOnlyList<ProjectVerificationCheck>> ContributeAsync(
        ProjectCheckContext context,
        CancellationToken cancellationToken)
    {
        var domains = await _db.ProjectDomains
            .AsNoTracking()
            .Where(d => d.ProjectId == context.Project.Id && d.Status == DomainStatus.Active)
            .ToListAsync(cancellationToken);

        var checks = new List<ProjectVerificationCheck>();

        foreach (var domain in domains)
        {
            checks.Add(await CheckCertificateAsync(domain, cancellationToken));

            var dnsCheck = await CheckDnsAsync(domain, cancellationToken);
            if (dnsCheck is not null)
            {
                checks.Add(dnsCheck);
            }
        }

        return checks;
    }

    private async Task<ProjectVerificationCheck> CheckCertificateAsync(
        ProjectDomain domain,
        CancellationToken cancellationToken)
    {
        var checkId = $"domain.certificate:{domain.Hostname}";
        var label = $"Certificate for {domain.DisplayHostname}";
        var url = $"https://{domain.Hostname}";

        var inspection = await _certificates.InspectAsync(domain.Hostname, cancellationToken);

        if (inspection.IsInconclusive)
        {
            // The host did not complete a handshake. That is not the same as a bad certificate, and
            // saying so keeps a transient outage from reading as "your HTTPS is broken".
            return Check(checkId, label, VerificationCheckStatus.Inconclusive, domain,
                $"DeployAI could not reach {domain.DisplayHostname} over HTTPS to check its certificate.", url);
        }

        if (!inspection.IsIssued)
        {
            return Check(checkId, label, VerificationCheckStatus.Failed, domain,
                $"{domain.DisplayHostname} is not serving a certificate a browser will accept ({inspection.Outcome}).",
                url);
        }

        // The renewal window is the whole reason to keep checking a domain that already works: a
        // certificate is valid right up until it is not, and the only warning is the clock.
        if (inspection.NotAfter is { } expiry)
        {
            var daysLeft = (expiry - DateTimeOffset.UtcNow).TotalDays;
            if (daysLeft <= _options.CertificateExpiryWarningDays)
            {
                return Check(checkId, label, VerificationCheckStatus.Warning, domain,
                    $"The certificate for {domain.DisplayHostname} expires in {Math.Max(0, (int)daysLeft)} day(s).",
                    url);
            }

            return Check(checkId, label, VerificationCheckStatus.Passed, domain,
                $"Valid certificate, expiring in {(int)daysLeft} day(s).", url);
        }

        return Check(checkId, label, VerificationCheckStatus.Passed, domain,
            "Serving a valid certificate.", url);
    }

    private async Task<ProjectVerificationCheck?> CheckDnsAsync(
        ProjectDomain domain,
        CancellationToken cancellationToken)
    {
        var checkId = $"domain.dns:{domain.Hostname}";
        var label = $"DNS for {domain.DisplayHostname}";

        if (string.IsNullOrWhiteSpace(domain.ExpectedAddress))
        {
            // Nothing recorded to compare against, so there is no check to run — as opposed to a
            // check that ran and found nothing.
            return Check(checkId, label, VerificationCheckStatus.Skipped, domain,
                "No server address was recorded for this domain, so its DNS cannot be compared.");
        }

        var result = await _dns.CheckAsync(domain.Hostname, domain.ExpectedAddress, cancellationToken);

        if (result.IsInconclusive)
        {
            return Check(checkId, label, VerificationCheckStatus.Inconclusive, domain,
                $"No DNS resolver answered for {domain.DisplayHostname}, so its record could not be checked.");
        }

        if (result.PointsAtTarget)
        {
            return Check(checkId, label, VerificationCheckStatus.Passed, domain,
                $"{domain.DisplayHostname} resolves to this app's server.");
        }

        if (result.IsProxiedByCdn)
        {
            // Not wrong, just proxied — and the remediation is unguessable, so it gets its own words.
            return Check(checkId, label, VerificationCheckStatus.Warning, domain,
                $"{domain.DisplayHostname} resolves to a CDN rather than this app's server. "
                + "If certificates stop renewing, set the record to DNS-only.");
        }

        var observed = result.ObservedAddresses.Count == 0
            ? "nothing"
            : string.Join(", ", result.ObservedAddresses);

        return Check(checkId, label, VerificationCheckStatus.Failed, domain,
            $"{domain.DisplayHostname} resolves to {observed}, not this app's server "
            + $"({domain.ExpectedAddress}).");
    }

    private static ProjectVerificationCheck Check(
        string checkId,
        string label,
        VerificationCheckStatus status,
        ProjectDomain domain,
        string message,
        string? url = null) =>
        new(checkId, VerificationCheckTargets.Domain, label, status, message, url, null, domain.DeployTargetId);
}
