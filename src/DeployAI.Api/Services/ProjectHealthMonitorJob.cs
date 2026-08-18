using DeployAI.Data.Entities;

namespace DeployAI.Api.Services;

// ProjectHealthStatus and ProjectHealthState moved to DeployAI.Core.Deployments so the Data layer can
// use the same vocabulary as the entities that now persist it.

/// <summary>Scheduled job that re-verifies every deployed project and records what it found.</summary>
/// <remarks>
/// The class name and the recurring-job id are deliberately unchanged: Hangfire persists the job's
/// type name in Postgres, so renaming this type orphans the schedule already stored there and the
/// sweep silently stops running. The work moved to <see cref="IFleetVerificationService"/>; only the
/// entry point stayed.
/// </remarks>
public sealed class ProjectHealthMonitorJob
{
    private readonly IFleetVerificationService _fleet;

    public ProjectHealthMonitorJob(IFleetVerificationService fleet)
    {
        _fleet = fleet;
    }

    /// <summary>Entry point invoked on schedule: sweeps every project.</summary>
    public Task RunAsync(CancellationToken cancellationToken) =>
        _fleet.SweepAsync(userId: null, VerificationRunTriggers.Scheduled, cancellationToken);

    /// <summary>Entry point for an on-demand sweep of one user's projects.</summary>
    public Task RunForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        _fleet.SweepAsync(userId, VerificationRunTriggers.Manual, cancellationToken);
}
