using DeployAI.Data;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>Scheduled job that periodically drift-checks (without applying changes) every Vercel+Railway project's cross-provider env wiring, so drift surfaces proactively rather than only when a user notices something's broken.</summary>
public sealed class EnvironmentDriftCheckJob
{
    private readonly DeployAIDbContext _db;
    private readonly IFrontendEnvironmentWiringService _frontendEnvironmentWiring;

    public EnvironmentDriftCheckJob(
        DeployAIDbContext db,
        IFrontendEnvironmentWiringService frontendEnvironmentWiring)
    {
        _db = db;
        _frontendEnvironmentWiring = frontendEnvironmentWiring;
    }

    /// <summary>Entry point invoked on schedule: finds every Vercel+Railway project and runs a drift-only env sync for each.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Any multi-target project, not just Railway+Vercel — a Coolify split full-stack drifts
        // too. Roles live in ConfigJson (not SQL-queryable), so the cheap DB filter is "more
        // than one target" and SyncCrossProviderEnvironmentAsync self-skips the rest: a
        // single-origin compose app (one target), an app+database pair (no server role), and
        // any unsupported provider pairing all short-circuit before doing work.
        var projectIds = await _db.Projects
            .AsNoTracking()
            .Where(p => p.DeployTargets.Count > 1)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        foreach (var projectId in projectIds)
        {
            var driftResult = await _frontendEnvironmentWiring.SyncCrossProviderEnvironmentAsync(
                projectId,
                new EnvironmentSyncOptions(
                    DetectDriftOnly: true,
                    RunVerification: false,
                    Source: "scheduled"),
                cancellationToken);

            if (!driftResult.DriftDetected || driftResult.Skipped)
            {
                continue;
            }

            await _frontendEnvironmentWiring.SyncCrossProviderEnvironmentAsync(
                projectId,
                new EnvironmentSyncOptions(
                    RedeployRailwayAfterUpdate: true,
                    EnsureWebsiteWiring: true,
                    ApplyVercelEnv: true,
                    ApplyRailwayEnv: true,
                    RunVerification: false,
                    Source: "scheduled"),
                cancellationToken);
        }
    }
}
