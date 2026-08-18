using DeployAI.Api.Services;
using DeployAI.Api.Services.Checks;
using DeployAI.Core.Deployments;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// The scheduled sweep that is supposed to tell a user their deployed apps still work.
/// </summary>
/// <remarks>
/// Neither recurring job in this codebase had a single test, which is why both of the first two bugs
/// below survived: the sweep abandoned every remaining project the moment one of them threw, and it
/// skipped entirely any project whose last publish did not succeed — the projects most likely to be
/// broken. Both were observed failing against the unfixed code before the fix was written.
/// <para>
/// These build a real DI graph rather than mocking the sweep's collaborators, because the failure
/// mode being guarded against is a wiring one: a scoped DbContext shared across concurrent projects
/// throws only under load, and only sometimes.
/// </para>
/// </remarks>
public class FleetVerificationServiceTests
{
    /// <summary>
    /// A provider call that throws for one project must not silence every project after it.
    /// </summary>
    /// <remarks>
    /// The loop had no try/catch, so a single unreachable Coolify instance — or one project whose
    /// credential was revoked — aborted the whole hourly sweep. Every project ordered after it kept
    /// its previous health indefinitely and still read as "checked", which is the failure a monitor
    /// exists to prevent.
    /// </remarks>
    [Fact]
    public async Task OneProjectThrowing_DoesNotStopTheSweep()
    {
        var harness = new SweepHarness();
        var first = harness.SeedProject("first");
        var throwing = harness.SeedProject("throwing");
        var last = harness.SeedProject("last");
        await harness.SaveAsync();

        harness.ThrowsFor(throwing.DeploymentId, new InvalidOperationException("Coolify is unreachable"));

        var summary = await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        Assert.Equal(3, summary.ProjectsChecked);
        Assert.NotNull(await harness.HealthOf(first.ProjectId));
        Assert.NotNull(await harness.HealthOf(last.ProjectId));

        // The one that threw is recorded too — as unable to be checked, naming the exception type
        // and not its text, rather than left silent or reported as broken.
        var health = await harness.HealthOf(throwing.ProjectId);
        Assert.NotNull(health);
        Assert.Equal(ProjectHealthStatus.Inconclusive, ProjectHealthState.Parse(health)!.Status);

        // Attributed to the family of checks that broke rather than to the whole project: the other
        // contributors still ran, so blaming the sweep would overstate what was lost.
        var failedCheck = await harness.CheckState(
            throwing.ProjectId, ProjectVerificationService.ContributorCheckId("live URLs"));
        Assert.Equal(VerificationCheckStatus.Inconclusive, failedCheck.Status);
        Assert.Contains("InvalidOperationException", failedCheck.Message, StringComparison.Ordinal);
        // The exception's type, never its text — a provider's raw error is not a user-facing message.
        Assert.DoesNotContain("Coolify is unreachable", failedCheck.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A project whose last publish failed is exactly the one worth checking, and was the one project
    /// the sweep never looked at.
    /// </summary>
    /// <remarks>
    /// The old sweep returned as soon as no <c>success</c> deployment was found, so nothing was
    /// recorded at all: no health, no status, no reason. The dashboard could not distinguish that
    /// project from one that had never been checked.
    /// </remarks>
    [Fact]
    public async Task AProjectWhoseLastDeployFailed_IsStillChecked()
    {
        var harness = new SweepHarness();
        var project = harness.SeedProject("last-deploy-failed", DeploymentStatuses.Failed);
        await harness.SaveAsync();

        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        Assert.NotNull(await harness.HealthOf(project.ProjectId));

        // And it says which absence it is: no live address to probe yet, rather than a silent skip.
        var check = await harness.CheckState(project.ProjectId, "deployment.never_succeeded");
        Assert.Equal(VerificationCheckStatus.Inconclusive, check.Status);
    }

    /// <summary>
    /// Every check coming back inconclusive means DeployAI learned nothing — not that the project is
    /// fine, and not that it has never been looked at.
    /// </summary>
    /// <remarks>
    /// The old rollup mapped "no failures, no passes" to <c>Unknown</c>, which the UI renders as "not
    /// checked yet". A project DeployAI had entirely lost sight of was therefore indistinguishable
    /// from one it had never checked.
    /// </remarks>
    [Fact]
    public async Task AllChecksInconclusive_RollsUpToInconclusive_NotHealthy()
    {
        var harness = new SweepHarness();
        var project = harness.SeedProject("blind");
        await harness.SaveAsync();

        harness.Returns(project.DeploymentId, ("server.health", "unrecognised-status"));

        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        var health = ProjectHealthState.Parse(await harness.HealthOf(project.ProjectId))!;
        Assert.Equal(ProjectHealthStatus.Inconclusive, health.Status);
        Assert.NotEqual(ProjectHealthStatus.Unknown, health.Status);
        Assert.NotEqual(ProjectHealthStatus.Healthy, health.Status);
    }

    /// <summary>A healthy project is re-checked every sweep; "already fine" is not a reason to skip.</summary>
    /// <remarks>
    /// The shape this guards against has produced six separate bugs in this codebase: an operation
    /// wired to run only when a resource is created never reaches the resources that already exist.
    /// </remarks>
    [Fact]
    public async Task EverySweepRechecksEveryProject_IncludingHealthyOnes()
    {
        var harness = new SweepHarness();
        var project = harness.SeedProject("healthy");
        await harness.SaveAsync();

        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        await using var db = harness.NewContext();
        var runs = await db.ProjectVerificationRuns.CountAsync(r => r.ProjectId == project.ProjectId);
        Assert.Equal(3, runs);
    }

    /// <summary>
    /// Concurrent projects must not share a DbContext.
    /// </summary>
    /// <remarks>
    /// The sweep this replaced held one injected <c>DeployAIDbContext</c> and iterated, which was
    /// only correct because it never ran two projects at once. Running the real DI graph at a
    /// parallelism above one is the only way this shows up: a shared context throws
    /// "A second operation started on this context" intermittently, and only under load.
    /// </remarks>
    [Fact]
    public async Task ProjectsVerifiedConcurrently_DoNotShareADbContext()
    {
        var harness = new SweepHarness(maxDegreeOfParallelism: 4);
        for (var i = 0; i < 8; i++)
        {
            harness.SeedProject($"project-{i}");
        }

        await harness.SaveAsync();

        var summary = await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        Assert.Equal(8, summary.ProjectsChecked);
        Assert.Equal(0, summary.ProjectsErrored);

        await using var db = harness.NewContext();
        Assert.Equal(8, await db.ProjectVerificationRuns.CountAsync());
    }

    /// <summary>Host shutdown stops the sweep rather than recording every remaining project as broken.</summary>
    [Fact]
    public async Task ACancelledSweep_StopsRatherThanRecordingFailures()
    {
        var harness = new SweepHarness();
        harness.SeedProject("one");
        harness.SeedProject("two");
        await harness.SaveAsync();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, cancelled.Token));

        await using var db = harness.NewContext();
        Assert.Equal(0, await db.ProjectVerificationRuns.CountAsync());
    }

    /// <summary>History accumulates per check, which is what makes a regression visible at all.</summary>
    [Fact]
    public async Task CheckResultsAccumulate_SoAPassYesterdayIsComparableToAFailToday()
    {
        var harness = new SweepHarness();
        var project = harness.SeedProject("regressing");
        await harness.SaveAsync();

        harness.Returns(project.DeploymentId, ("server.health", "passed"));
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        harness.Returns(project.DeploymentId, ("server.health", "failed"));
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        await using var db = harness.NewContext();
        var history = await db.ProjectVerificationCheckResults
            .Where(r => r.ProjectId == project.ProjectId && r.CheckId == "server.health")
            .OrderBy(r => r.ObservedAt)
            .ToListAsync();

        Assert.Equal(2, history.Count);
        Assert.Equal(VerificationCheckStatus.Passed, history[0].Status);
        Assert.Equal(VerificationCheckStatus.Failed, history[1].Status);

        var state = await harness.CheckState(project.ProjectId, "server.health");
        Assert.Equal(VerificationCheckStatus.Failed, state.Status);
        Assert.Equal(1, state.ConsecutiveFailures);
    }

    /// <summary>
    /// A check that starts failing is reported once, and staying failed is not reported again.
    /// </summary>
    /// <remarks>
    /// End to end through the real sweep rather than against the transition rules alone, because the
    /// thing that would break silently is the wiring: a recorder that returned transitions nobody
    /// passed on would leave the whole notification path dead with every unit test still green.
    /// </remarks>
    [Fact]
    public async Task ACheckThatStartsFailing_IsReportedOnce_AndNotAgainWhileItStaysFailing()
    {
        var harness = new SweepHarness();
        var project = harness.SeedProject("regressing");
        await harness.SaveAsync();

        harness.Returns(project.DeploymentId, ("server.health", "passed"));
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);
        Assert.Empty(harness.Notifications.Sent);

        harness.Returns(project.DeploymentId, ("server.health", "failed"));
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        var first = Assert.Single(harness.Notifications.Sent);
        Assert.Equal(CheckNotification.StartedFailing, first.Transition.Notification);
        Assert.Equal(project.ProjectId, first.ProjectId);

        // Still failing on the next sweep: no second email.
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);
        Assert.Single(harness.Notifications.Sent);

        harness.Returns(project.DeploymentId, ("server.health", "passed"));
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        Assert.Equal(2, harness.Notifications.Sent.Count);
        Assert.Equal(CheckNotification.Recovered, harness.Notifications.Sent[1].Transition.Notification);
    }

    /// <summary>
    /// A provider going unreachable mid-outage must not produce a "recovered" the user would read as
    /// good news.
    /// </summary>
    [Fact]
    public async Task AProviderBlipDuringAnOutage_DoesNotReportARecovery()
    {
        var harness = new SweepHarness();
        var project = harness.SeedProject("blipping");
        await harness.SaveAsync();

        harness.Returns(project.DeploymentId, ("server.health", "failed"));
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        // The provider stops answering: the checks come back unrecognised, which maps to inconclusive.
        harness.Returns(project.DeploymentId, ("server.health", "unrecognised-status"));
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        // And the outage is still there when it returns.
        harness.Returns(project.DeploymentId, ("server.health", "failed"));
        await harness.Fleet.SweepAsync(null, VerificationRunTriggers.Scheduled, default);

        var only = Assert.Single(harness.Notifications.Sent);
        Assert.Equal(CheckNotification.StartedFailing, only.Transition.Notification);
        Assert.DoesNotContain(
            harness.Notifications.Sent,
            n => n.Transition.Notification == CheckNotification.Recovered);
    }
}

/// <summary>
/// A real DI graph around the sweep, with only the live-URL probes faked.
/// </summary>
/// <remarks>
/// Everything below the sweep is the production wiring — the scope factory, the scoped runner, the
/// recorder, the rollup — because the bugs worth catching here are wiring bugs. Only
/// <see cref="IDeploymentVerificationService"/> is a mock, since it is the one collaborator that
/// makes real HTTP calls.
/// </remarks>
internal sealed class SweepHarness
{
    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly Mock<IDeploymentVerificationService> _verification = new();
    private readonly ServiceProvider _services;
    private readonly DeployAIDbContext _seedContext;

    public SweepHarness(int maxDegreeOfParallelism = 1)
    {
        _verification
            .Setup(v => v.VerifyAsync(
                It.IsAny<Guid>(), It.IsAny<DeploymentVerificationScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(("server.health", "passed")));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddDbContext<DeployAIDbContext>(o => o.UseInMemoryDatabase(_databaseName));
        services.Configure<FleetVerificationOptions>(o =>
        {
            o.MaxDegreeOfParallelism = maxDegreeOfParallelism;
            o.PerProjectTimeoutSeconds = 30;
        });
        services.AddSingleton(_verification.Object);
        services.AddScoped<IProjectVerificationRecorder, ProjectVerificationRecorder>();
        services.AddScoped<IProjectVerificationService, ProjectVerificationService>();
        services.AddScoped<IProjectCheckContributor, DeploymentUrlChecks>();
        services.AddScoped<IProjectSweepRunner, ProjectSweepRunner>();
        services.AddSingleton<IFleetVerificationService, FleetVerificationService>();
        services.AddSingleton<IFleetHealthNotificationService>(Notifications);

        _services = services.BuildServiceProvider();
        _seedContext = NewContext();
    }

    public IFleetVerificationService Fleet => _services.GetRequiredService<IFleetVerificationService>();

    /// <summary>Records what the sweep would have told the user, without sending anything.</summary>
    public RecordingNotifications Notifications { get; } = new();

    public DeployAIDbContext NewContext() =>
        new(new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options);

    public void ThrowsFor(Guid deploymentId, Exception exception) =>
        _verification
            .Setup(v => v.VerifyAsync(
                deploymentId, It.IsAny<DeploymentVerificationScope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

    public void Returns(Guid deploymentId, params (string Id, string Status)[] checks) =>
        _verification
            .Setup(v => v.VerifyAsync(
                deploymentId, It.IsAny<DeploymentVerificationScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(checks));

    public Task SaveAsync() => _seedContext.SaveChangesAsync();

    public async Task<string?> HealthOf(Guid projectId)
    {
        await using var db = NewContext();
        return (await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId)).HealthJson;
    }

    public async Task<ProjectCheckState> CheckState(Guid projectId, string checkId)
    {
        await using var db = NewContext();
        return await db.ProjectCheckStates
            .AsNoTracking()
            .FirstAsync(s => s.ProjectId == projectId && s.CheckId == checkId);
    }

    /// <summary>Seeds one project with a deploy target (so the sweep picks it up) and one deployment.</summary>
    public (Guid ProjectId, Guid DeploymentId) SeedProject(
        string name,
        string deploymentStatus = DeploymentStatuses.Success)
    {
        var projectId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();

        _seedContext.Projects.Add(new Project
        {
            Id = projectId,
            UserId = Guid.NewGuid(),
            Name = name,
            GitHubRepoFullName = $"tester/{name}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        _seedContext.DeployTargets.Add(new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ProviderName = "coolify",
            CredentialId = credentialId,
            ProviderProjectId = $"app-{name}",
            ConfigJson = """{"role":"server"}""",
            CreatedAt = DateTimeOffset.UtcNow
        });

        _seedContext.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            ProjectId = projectId,
            Branch = "main",
            Status = deploymentStatus,
            CreatedAt = DateTimeOffset.UtcNow
        });

        return (projectId, deploymentId);
    }

    /// <summary>Every notification the sweep raised, in order, so edges can be asserted.</summary>
    public sealed class RecordingNotifications : IFleetHealthNotificationService
    {
        private readonly List<(Guid ProjectId, CheckTransition Transition)> _sent = [];

        public IReadOnlyList<(Guid ProjectId, CheckTransition Transition)> Sent
        {
            get { lock (_sent) { return _sent.ToList(); } }
        }

        public Task NotifyAsync(
            Guid projectId,
            IReadOnlyList<CheckTransition> transitions,
            CancellationToken cancellationToken)
        {
            lock (_sent)
            {
                _sent.AddRange(transitions.Select(t => (projectId, t)));
            }

            return Task.CompletedTask;
        }
    }

    private static DeploymentVerificationResult Result(params (string Id, string Status)[] checks) =>
        new(
            Success: checks.All(c => c.Status == "passed"),
            Scope: "both",
            Checks: checks
                .Select(c => new DeploymentVerificationCheck(
                    c.Id, "server", "API health", c.Status, "Checked.", null, null, false, []))
                .ToList(),
            CompletedAt: DateTimeOffset.UtcNow);
}
