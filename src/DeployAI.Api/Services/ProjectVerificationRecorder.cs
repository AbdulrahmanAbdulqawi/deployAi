using DeployAI.Core.Deployments;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

/// <summary>One check whose conclusive answer changed, and what the user should be told about it.</summary>
public sealed record CheckTransition(
    string CheckId,
    string Label,
    VerificationCheckStatus Status,
    string Message,
    CheckNotification Notification);

/// <summary>Everything one recorded run produced, including what is worth notifying about.</summary>
public sealed record RecordedVerificationRun(
    Guid RunId,
    ProjectHealthStatus Outcome,
    string Summary,
    IReadOnlyList<CheckTransition> Transitions);

public interface IProjectVerificationRecorder
{
    /// <summary>Persists one project's verification: the run, every check result, and the state upserts.</summary>
    Task<RecordedVerificationRun> RecordAsync(
        Guid projectId,
        Guid? deploymentId,
        string trigger,
        IReadOnlyList<ProjectVerificationCheck> checks,
        bool sweepErrored,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Writes a verification run to the tables that make regressions visible.
/// </summary>
/// <remarks>
/// Verification used to be computed and discarded, so the only question anyone actually asks —
/// "was this working yesterday?" — had no answer anywhere. Three writes per run: the run row, one
/// result row per check (the history), and an upsert per check state (the current picture and the
/// notification ledger). All in one <c>SaveChangesAsync</c>, so a crash costs a whole run rather
/// than half of one.
/// </remarks>
public sealed class ProjectVerificationRecorder : IProjectVerificationRecorder
{
    private readonly DeployAIDbContext _db;
    private readonly FleetVerificationOptions _options;

    public ProjectVerificationRecorder(DeployAIDbContext db, IOptions<FleetVerificationOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<RecordedVerificationRun> RecordAsync(
        Guid projectId,
        Guid? deploymentId,
        string trigger,
        IReadOnlyList<ProjectVerificationCheck> checks,
        bool sweepErrored,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var (outcome, summary) = ProjectHealthRollup.Roll(checks);
        var runId = Guid.NewGuid();

        var run = new ProjectVerificationRun
        {
            Id = runId,
            ProjectId = projectId,
            DeploymentId = deploymentId,
            Trigger = trigger,
            Outcome = outcome,
            SweepErrored = sweepErrored,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = (int)Math.Clamp((completedAt - startedAt).TotalMilliseconds, 0, int.MaxValue),
            PassedChecks = checks.Count(c => c.Status == VerificationCheckStatus.Passed),
            FailedChecks = checks.Count(c => c.Status == VerificationCheckStatus.Failed),
            WarningChecks = checks.Count(c => c.Status == VerificationCheckStatus.Warning),
            SkippedChecks = checks.Count(c => c.Status == VerificationCheckStatus.Skipped),
            InconclusiveChecks = checks.Count(c => c.Status == VerificationCheckStatus.Inconclusive),
            Summary = summary
        };
        _db.ProjectVerificationRuns.Add(run);

        foreach (var check in checks)
        {
            _db.ProjectVerificationCheckResults.Add(new ProjectVerificationCheckResult
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                ProjectId = projectId,
                DeployTargetId = check.DeployTargetId,
                CheckId = check.CheckId,
                Target = check.Target,
                Label = check.Label,
                Status = check.Status,
                Message = check.Message,
                Url = check.Url,
                SuggestedAction = check.SuggestedAction,
                ObservedAt = completedAt
            });
        }

        var transitions = await UpsertStatesAsync(projectId, checks, completedAt, cancellationToken);

        await UpdateProjectHealthAsync(projectId, run, outcome, summary, completedAt, cancellationToken);
        await PruneAsync(projectId, completedAt, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return new RecordedVerificationRun(runId, outcome, summary, transitions);
    }

    /// <summary>
    /// Advances every check's ledger. Runs for every check on every sweep, including the ones that
    /// passed: "already fine" is the case that needs re-checking, not the case to skip.
    /// </summary>
    private async Task<IReadOnlyList<CheckTransition>> UpsertStatesAsync(
        Guid projectId,
        IReadOnlyList<ProjectVerificationCheck> checks,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var existing = await _db.ProjectCheckStates
            .Where(s => s.ProjectId == projectId)
            .ToDictionaryAsync(s => s.CheckId, cancellationToken);

        var transitions = new List<CheckTransition>();

        foreach (var check in checks)
        {
            if (!existing.TryGetValue(check.CheckId, out var state))
            {
                state = new ProjectCheckState
                {
                    ProjectId = projectId,
                    CheckId = check.CheckId,
                    FirstObservedAt = observedAt
                };
                _db.ProjectCheckStates.Add(state);
                existing[check.CheckId] = state;
            }

            var transition = CheckLedgerTransitions.Apply(
                // A brand-new row has no history, and passing its empty ledger in would read as
                // "previously skipped" — which would make a check that starts out failing look like
                // a transition from something.
                state.LastObservedAt == default ? null : state.ToLedger(),
                check.Status,
                observedAt,
                _options.InconclusiveRunsBeforeNotify);

            state.ApplyLedger(transition.State);
            state.DeployTargetId = check.DeployTargetId;
            state.Target = check.Target;
            state.Label = check.Label;
            state.Message = check.Message;
            state.Url = check.Url;
            state.SuggestedAction = check.SuggestedAction;
            state.LastObservedAt = observedAt;

            if (transition.Notification != CheckNotification.None)
            {
                transitions.Add(new CheckTransition(
                    check.CheckId, check.Label, check.Status, check.Message, transition.Notification));
            }
        }

        return transitions;
    }

    /// <summary>Rewrites the denormalised blob the project list and health banner read.</summary>
    private async Task UpdateProjectHealthAsync(
        Guid projectId,
        ProjectVerificationRun run,
        ProjectHealthStatus outcome,
        string summary,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        project.HealthJson = new ProjectHealthState
        {
            LastCheckedAt = observedAt,
            Status = outcome,
            PassedChecks = run.PassedChecks,
            TotalChecks = run.PassedChecks + run.FailedChecks + run.WarningChecks
                          + run.SkippedChecks + run.InconclusiveChecks,
            FailedChecks = run.FailedChecks,
            WarningChecks = run.WarningChecks,
            InconclusiveChecks = run.InconclusiveChecks,
            SkippedChecks = run.SkippedChecks,
            Summary = summary,
            DeploymentId = run.DeploymentId,
            RunId = run.Id
        }.ToJson();
        project.UpdatedAt = observedAt;
    }

    /// <summary>
    /// Keeps history bounded without losing the recent picture: age-based pruning with a floor, so a
    /// project checked once a month still has its last runs to compare against.
    /// </summary>
    private async Task PruneAsync(Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-_options.RunRetentionDays);

        var expendable = await _db.ProjectVerificationRuns
            .Where(r => r.ProjectId == projectId && r.StartedAt < cutoff)
            .OrderByDescending(r => r.StartedAt)
            .Skip(_options.MinimumRunsKept)
            .ToListAsync(cancellationToken);

        if (expendable.Count > 0)
        {
            // Results cascade from the run.
            _db.ProjectVerificationRuns.RemoveRange(expendable);
        }

        // A check that stopped being produced — a deploy target removed, a domain retired — would
        // otherwise sit in the fleet view at whatever status it last had, forever, with a
        // LastObservedAt nobody looks at. Age it out rather than freezing it.
        var staleBefore = now.AddDays(-_options.StaleCheckRetentionDays);
        var stale = await _db.ProjectCheckStates
            .Where(s => s.ProjectId == projectId && s.LastObservedAt < staleBefore)
            .ToListAsync(cancellationToken);

        if (stale.Count > 0)
        {
            _db.ProjectCheckStates.RemoveRange(stale);
        }
    }
}
