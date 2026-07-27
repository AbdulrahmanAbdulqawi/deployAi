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
        var projectIds = await _db.Projects
            .AsNoTracking()
            .Include(p => p.DeployTargets)
            .Where(p => p.DeployTargets.Any(t => t.ProviderName == "railway") &&
                        p.DeployTargets.Any(t => t.ProviderName == "vercel"))
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
