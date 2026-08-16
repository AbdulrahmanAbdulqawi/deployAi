using DeployAI.Data.Entities;
using Hangfire;

namespace DeployAI.Api.Services;

/// <summary>
/// The waiting rules for a domain: how long between checks, and how long before giving up.
/// </summary>
/// <remarks>
/// Pure and separate from the job so the arithmetic can be tested without a clock, a database, or
/// a background-job client.
/// </remarks>
public static class DomainReconciliation
{
    /// <summary>
    /// Backoff for DNS. Starts tight because a record written moments ago often appears within
    /// seconds, and stretches out because the slow case is a registrar's nameservers propagating,
    /// which polling faster does not hurry.
    /// </summary>
    public static readonly TimeSpan[] DnsBackoff =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10)
    ];

    /// <summary>Tighter: a certificate that is going to issue does so in seconds, not minutes.</summary>
    public static readonly TimeSpan[] CertificateBackoff =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(40),
        TimeSpan.FromSeconds(60)
    ];

    /// <summary>An hour, because the slow case is a registrar's nameservers propagating.</summary>
    public static readonly TimeSpan DnsDeadline = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Ten minutes when DeployAI wrote the record itself: it set a one-minute TTL, so anything
    /// past this is the DNS provider misbehaving rather than propagation still running.
    /// </summary>
    public static readonly TimeSpan ManagedDnsDeadline = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ten minutes. A challenge that is going to succeed takes under two, so a longer wait only
    /// spends more of Let's Encrypt's five-failures-an-hour budget arriving at the same answer.
    /// </summary>
    public static readonly TimeSpan CertificateDeadline = TimeSpan.FromMinutes(10);

    /// <summary>The final rung repeats rather than running off the end of the ladder.</summary>
    public static TimeSpan DelayFor(TimeSpan[] ladder, int attempt) =>
        ladder[Math.Clamp(attempt, 0, ladder.Length - 1)];

    public static TimeSpan DelayFor(DomainStatus status, int attempt) =>
        status is DomainStatus.CertificatePending or DomainStatus.Assigned
            ? DelayFor(CertificateBackoff, attempt)
            : DelayFor(DnsBackoff, attempt);

    public static bool IsTerminal(DomainStatus status) =>
        status is DomainStatus.Active
            or DomainStatus.DnsFailed
            or DomainStatus.DnsUnverifiable
            or DomainStatus.CertificateFailed
            or DomainStatus.CertificateUnverifiable
            or DomainStatus.Conflicted
            or DomainStatus.Retired;
}

/// <summary>Starts a domain moving. Exists so the reset rules live in exactly one place.</summary>
public interface IDomainReconciliationScheduler
{
    void Start(Guid domainId);
}

public sealed class DomainReconciliationScheduler : IDomainReconciliationScheduler
{
    private readonly IBackgroundJobClient _backgroundJobs;

    public DomainReconciliationScheduler(IBackgroundJobClient backgroundJobs) =>
        _backgroundJobs = backgroundJobs;

    public void Start(Guid domainId) =>
        _backgroundJobs.Enqueue<DomainReconciliationJob>(
            job => job.RunAsync(domainId, 0, CancellationToken.None));
}

/// <summary>
/// Advances one domain by one step and books its own next visit, until the domain is finished or
/// its deadline passes.
/// </summary>
/// <remarks>
/// <para>
/// This is the codebase's first polling loop, and it is deliberately a self-rescheduling Hangfire
/// job rather than a hosted service: deploys already work this way, the schedule survives a
/// restart, and there is no extra process to keep alive.
/// </para>
/// <para>
/// <c>AutomaticRetry(Attempts = 0)</c> is load-bearing. Hangfire's default is ten retries on its
/// own exponential ladder, which against a job that already reschedules itself would produce two
/// independent ladders, two clocks, and duplicate writes to the same provider.
/// </para>
/// </remarks>
[AutomaticRetry(Attempts = 0)]
[DisableConcurrentExecution(timeoutInSeconds: 60)]
public sealed class DomainReconciliationJob
{
    private readonly IDomainService _domains;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<DomainReconciliationJob> _logger;

    public DomainReconciliationJob(
        IDomainService domains,
        IBackgroundJobClient backgroundJobs,
        ILogger<DomainReconciliationJob> logger)
    {
        _domains = domains;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    public async Task RunAsync(Guid domainId, int attempt, CancellationToken cancellationToken)
    {
        var result = await _domains.ReconcileOnceAsync(domainId, cancellationToken);

        if (!result.ShouldContinue)
        {
            _logger.LogInformation(
                "Domain {DomainId} finished at {Status}: {Message}", domainId, result.Status, result.Message);
            return;
        }

        // Persisted before scheduled, so a crash between the two costs a tick rather than leaving
        // two jobs racing the same row.
        _backgroundJobs.Schedule<DomainReconciliationJob>(
            job => job.RunAsync(domainId, attempt + 1, CancellationToken.None),
            DomainReconciliation.DelayFor(result.Status, attempt));
    }
}
