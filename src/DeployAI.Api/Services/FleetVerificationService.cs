using DeployAI.Core.Deployments;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

/// <summary>What one sweep covered.</summary>
/// <param name="ProjectsErrored">
/// Projects where the sweep itself broke. Counted separately from projects that were checked and
/// came back inconclusive: one is DeployAI failing at its own job, and a monitor that cannot count
/// that will not get fixed.
/// </param>
public sealed record FleetSweepSummary(
    int ProjectsChecked,
    int ProjectsErrored,
    DateTimeOffset CompletedAt);

public interface IFleetVerificationService
{
    /// <summary>Re-verifies every project, or every project belonging to one user.</summary>
    Task<FleetSweepSummary> SweepAsync(
        Guid? userId,
        string trigger,
        CancellationToken cancellationToken);

    /// <summary>Re-verifies one project and returns what was recorded.</summary>
    Task<RecordedVerificationRun> VerifyOneAsync(
        Guid projectId,
        string trigger,
        CancellationToken cancellationToken);
}

/// <summary>
/// Re-checks every deployed project on a schedule, so a regression surfaces before a user finds it.
/// </summary>
/// <remarks>
/// <para>
/// Singleton, taking <see cref="IServiceScopeFactory"/>, because the work runs concurrently and
/// <see cref="DeployAIDbContext"/> is neither thread-safe nor shareable across projects. Every
/// project gets its own scope, and therefore its own context. The sweep this replaced held one
/// injected context and iterated — correct only because it never ran two projects at once.
/// </para>
/// <para>
/// Every project is re-checked on every sweep, including the ones that passed last time. "Already
/// healthy" is the state that needs confirming, not the state to skip.
/// </para>
/// </remarks>
public sealed class FleetVerificationService : IFleetVerificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FleetVerificationOptions _options;
    private readonly ILogger<FleetVerificationService> _logger;

    public FleetVerificationService(
        IServiceScopeFactory scopeFactory,
        IOptions<FleetVerificationOptions> options,
        ILogger<FleetVerificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<FleetSweepSummary> SweepAsync(
        Guid? userId,
        string trigger,
        CancellationToken cancellationToken)
    {
        var projectIds = await LoadProjectIdsAsync(userId, cancellationToken);
        _logger.LogInformation("Fleet sweep starting for {ProjectCount} projects.", projectIds.Count);

        var errored = 0;
        using var throttle = new SemaphoreSlim(Math.Max(1, _options.MaxDegreeOfParallelism));

        await Task.WhenAll(projectIds.Select(async projectId =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var run = await VerifyOneAsync(projectId, trigger, cancellationToken);
                if (run.Outcome is ProjectHealthStatus.Unknown && run.Summary.Length == 0)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host shutdown. Stopping is correct; recording every remaining project as broken
                // because the process is going down is not.
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errored);
                _logger.LogError(ex, "Fleet sweep could not record project {ProjectId}.", projectId);
            }
            finally
            {
                throttle.Release();
            }
        }));

        var summary = new FleetSweepSummary(projectIds.Count, errored, DateTimeOffset.UtcNow);
        _logger.LogInformation(
            "Fleet sweep finished: {Checked} projects checked, {Errored} could not be recorded.",
            summary.ProjectsChecked, summary.ProjectsErrored);
        return summary;
    }

    public async Task<RecordedVerificationRun> VerifyOneAsync(
        Guid projectId,
        string trigger,
        CancellationToken cancellationToken)
    {
        // Its own scope, and therefore its own DbContext: two projects verified at once through one
        // shared context throws "a second operation started on this context", intermittently and
        // only under load.
        using var scope = _scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IProjectSweepRunner>();

        // A per-project budget, so one provider that accepts the connection and then never answers
        // costs this project's run rather than the whole sweep.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.PerProjectTimeoutSeconds)));

        return await runner.RunAsync(projectId, trigger, budget.Token, cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> LoadProjectIdsAsync(Guid? userId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeployAIDbContext>();

        return await db.Projects
            .AsNoTracking()
            .Where(p => p.DeployTargets.Any())
            .Where(p => userId == null || p.UserId == userId)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
    }
}

public interface IProjectSweepRunner
{
    /// <summary>
    /// Verifies one project and records the result, whatever happens.
    /// </summary>
    /// <param name="budgetToken">Cancels this project's checks when its time is up.</param>
    /// <param name="sweepToken">
    /// Cancels the whole sweep. Kept separate from the budget so a project that ran out of time is
    /// recorded as inconclusive, while a host shutdown stops without recording anything.
    /// </param>
    Task<RecordedVerificationRun> RunAsync(
        Guid projectId,
        string trigger,
        CancellationToken budgetToken,
        CancellationToken sweepToken);
}

/// <summary>
/// One project's turn through the sweep, isolated so that its failure is its own.
/// </summary>
/// <remarks>
/// Scoped, and the only holder of a <see cref="DeployAIDbContext"/> in the sweep. The isolation here
/// is the whole point: the sweep it replaced looped with no try/catch, so a single unreachable
/// provider aborted the run and every project ordered after it kept its previous health with nothing
/// to say it had not been looked at.
/// </remarks>
public sealed class ProjectSweepRunner : IProjectSweepRunner
{
    private readonly IProjectVerificationService _verification;
    private readonly IProjectVerificationRecorder _recorder;
    private readonly IFleetHealthNotificationService _notifications;
    private readonly ILogger<ProjectSweepRunner> _logger;

    public ProjectSweepRunner(
        IProjectVerificationService verification,
        IProjectVerificationRecorder recorder,
        IFleetHealthNotificationService notifications,
        ILogger<ProjectSweepRunner> logger)
    {
        _verification = verification;
        _recorder = recorder;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<RecordedVerificationRun> RunAsync(
        Guid projectId,
        string trigger,
        CancellationToken budgetToken,
        CancellationToken sweepToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        IReadOnlyList<ProjectVerificationCheck> checks;
        Guid? deploymentId = null;
        var errored = false;

        try
        {
            var result = await _verification.VerifyProjectAsync(projectId, budgetToken);
            deploymentId = result.DeploymentId;
            checks = result.Checks;
        }
        catch (OperationCanceledException) when (sweepToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The budget, not the host. The project was looked at and did not finish in time, which
            // is a thing worth recording rather than a reason to say nothing.
            _logger.LogWarning("Project {ProjectId} ran out of its verification budget.", projectId);
            errored = true;
            checks = [SweepCheck(
                VerificationCheckStatus.Inconclusive,
                "Checking this app took too long, so DeployAI stopped waiting. Nothing here says the app is broken.")];
        }
        catch (Exception ex)
        {
            // The exception's type, never its text: a stack trace or a connection string in a
            // user-facing message is how internals leak into a dashboard.
            _logger.LogError(ex, "Verification threw for project {ProjectId}.", projectId);
            errored = true;
            checks = [SweepCheck(
                VerificationCheckStatus.Inconclusive,
                $"DeployAI could not check this app this time ({ex.GetType().Name}).")];
        }

        // Recorded with the sweep's token rather than the budget's: a project whose checks timed out
        // must still get its result written, or the timeout leaves exactly the silence it is meant
        // to report.
        var run = await _recorder.RecordAsync(
            projectId,
            deploymentId,
            trigger,
            checks,
            errored,
            startedAt,
            DateTimeOffset.UtcNow,
            sweepToken);

        // After the save, never before: a notification about a state that failed to persist would be
        // one the dashboard then contradicts, and the ledger that suppresses the duplicate lives in
        // the same row that just failed to write.
        await _notifications.NotifyAsync(projectId, run.Transitions, sweepToken);

        return run;
    }

    private static ProjectVerificationCheck SweepCheck(VerificationCheckStatus status, string message) =>
        new("project.sweep", VerificationCheckTargets.Project, "Verification run", status, message);
}
