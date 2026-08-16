using DeployAI.Api.Services;
using DeployAI.Data.Entities;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// The codebase's first polling loop. The behaviour worth pinning is not that it retries, but that
/// it stops: a self-rescheduling job that never reaches a terminal state keeps calling a provider
/// and a certificate authority forever.
/// </summary>
public class DomainReconciliationJobTests
{
    private static (DomainReconciliationJob Job, Mock<IBackgroundJobClient> Jobs) Create(
        DomainTickResult result)
    {
        var domains = new Mock<IDomainService>();
        domains
            .Setup(d => d.ReconcileOnceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var jobs = new Mock<IBackgroundJobClient>();
        jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-id");

        return (
            new DomainReconciliationJob(domains.Object, jobs.Object, NullLogger<DomainReconciliationJob>.Instance),
            jobs);
    }

    [Fact]
    public async Task RunAsync_SchedulesItselfAgain_WhileTheDomainIsStillMoving()
    {
        var domainId = Guid.NewGuid();
        var (job, jobs) = Create(new DomainTickResult(DomainStatus.DnsPending, true, "waiting"));

        await job.RunAsync(domainId, 0, CancellationToken.None);

        jobs.Verify(j => j.Create(
                It.Is<Job>(scheduled =>
                    scheduled.Method.Name == nameof(DomainReconciliationJob.RunAsync) &&
                    (Guid)scheduled.Args[0]! == domainId &&
                    (int)scheduled.Args[1]! == 1),
                It.IsAny<ScheduledState>()),
            Times.Once);
    }

    // Every terminal state has to stop the loop, not just the happy one. A state added later that
    // forgot to would poll a provider until the process restarted.
    [Theory]
    [InlineData(DomainStatus.Active)]
    [InlineData(DomainStatus.DnsFailed)]
    [InlineData(DomainStatus.DnsUnverifiable)]
    [InlineData(DomainStatus.CertificateFailed)]
    [InlineData(DomainStatus.CertificateUnverifiable)]
    [InlineData(DomainStatus.Conflicted)]
    [InlineData(DomainStatus.Retired)]
    public async Task RunAsync_StopsScheduling_OnceTheDomainIsFinished(DomainStatus status)
    {
        var (job, jobs) = Create(new DomainTickResult(status, false, "done"));

        await job.RunAsync(Guid.NewGuid(), 0, CancellationToken.None);

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_UsesTheDnsBackoff_WhileWaitingForARecord()
    {
        var (job, jobs) = Create(new DomainTickResult(DomainStatus.DnsPending, true, "waiting"));

        await job.RunAsync(Guid.NewGuid(), 0, CancellationToken.None);

        jobs.Verify(j => j.Create(
                It.IsAny<Job>(),
                It.Is<ScheduledState>(state =>
                    state.EnqueueAt - DateTime.UtcNow > TimeSpan.FromSeconds(10) &&
                    state.EnqueueAt - DateTime.UtcNow < TimeSpan.FromSeconds(20))),
            Times.Once);
    }

    // A certificate that is going to issue does so in seconds, so waiting a DNS-length interval
    // between checks would report success minutes after it was true.
    [Fact]
    public async Task RunAsync_UsesTheTighterCertificateBackoff_WhileWaitingForIssuance()
    {
        var (job, jobs) = Create(new DomainTickResult(DomainStatus.CertificatePending, true, "waiting"));

        await job.RunAsync(Guid.NewGuid(), 0, CancellationToken.None);

        jobs.Verify(j => j.Create(
                It.IsAny<Job>(),
                It.Is<ScheduledState>(state =>
                    state.EnqueueAt - DateTime.UtcNow < TimeSpan.FromSeconds(15))),
            Times.Once);
    }
}

/// <summary>The waiting arithmetic, tested without a clock or a job client.</summary>
public class DomainReconciliationTests
{
    [Fact]
    public void DelayFor_WalksTheLadder()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), DomainReconciliation.DelayFor(DomainReconciliation.DnsBackoff, 0));
        Assert.Equal(TimeSpan.FromSeconds(30), DomainReconciliation.DelayFor(DomainReconciliation.DnsBackoff, 1));
        Assert.Equal(TimeSpan.FromMinutes(1), DomainReconciliation.DelayFor(DomainReconciliation.DnsBackoff, 2));
    }

    // Running off the end of the ladder must not throw, and must not reset to the tightest
    // interval either -- a domain that has been waiting an hour should not be polled every 15
    // seconds again.
    [Theory]
    [InlineData(6)]
    [InlineData(50)]
    [InlineData(int.MaxValue)]
    public void DelayFor_RepeatsTheFinalRung_PastTheEndOfTheLadder(int attempt)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            DomainReconciliation.DelayFor(DomainReconciliation.DnsBackoff, attempt));
    }

    [Fact]
    public void DelayFor_IsNeverNegative_ForAnOutOfRangeAttempt()
    {
        Assert.True(DomainReconciliation.DelayFor(DomainReconciliation.DnsBackoff, -5) > TimeSpan.Zero);
    }

    // Ten minutes, not an hour: a challenge that will succeed takes under two, so a longer wait
    // only spends more of Let's Encrypt's five-failures-an-hour budget reaching the same answer.
    [Fact]
    public void CertificateDeadline_IsShorterThanTheDnsDeadline()
    {
        Assert.True(DomainReconciliation.CertificateDeadline < DomainReconciliation.DnsDeadline);
    }

    [Fact]
    public void IsTerminal_CoversEveryStateTheLoopMustStopOn()
    {
        DomainStatus[] moving =
        [
            DomainStatus.Pending,
            DomainStatus.DnsPending,
            DomainStatus.DnsVerified,
            DomainStatus.Assigned,
            DomainStatus.CertificatePending
        ];

        foreach (var status in Enum.GetValues<DomainStatus>())
        {
            Assert.Equal(!moving.Contains(status), DomainReconciliation.IsTerminal(status));
        }
    }
}
