using DeployAI.Data;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>Scheduled job that periodically drift-checks every multi-target project's cross-provider env wiring, so drift surfaces proactively rather than only when a user notices something's broken.</summary>
/// <remarks>
/// Isolated per project for the same reason as the fleet sweep: this loop had no try/catch either,
/// so one project whose provider was unreachable silently skipped every project after it, every six
/// hours, with nothing recorded to say the check had not run. Fixing that in one of the two
/// identical loops and leaving the other is the mistake this codebase's own rules warn about.
/// </remarks>
public sealed class EnvironmentDriftCheckJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EnvironmentDriftCheckJob> _logger;

    public EnvironmentDriftCheckJob(
        IServiceScopeFactory scopeFactory,
        ILogger<EnvironmentDriftCheckJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Entry point invoked on schedule: finds every multi-target project and runs a drift-only env sync for each.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<Guid> projectIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeployAIDbContext>();

            // Any multi-target project, not just Railway+Vercel — a Coolify split full-stack drifts
            // too. Roles live in ConfigJson (not SQL-queryable), so the cheap DB filter is "more
            // than one target" and SyncCrossProviderEnvironmentAsync self-skips the rest: a
            // single-origin compose app (one target), an app+database pair (no server role), and
            // any unsupported provider pairing all short-circuit before doing work.
            projectIds = await db.Projects
                .AsNoTracking()
                .Where(p => p.DeployTargets.Count > 1)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);
        }

        var errored = 0;
        foreach (var projectId in projectIds)
        {
            try
            {
                await CheckProjectAsync(projectId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errored++;
                _logger.LogError(ex, "Environment drift check failed for project {ProjectId}.", projectId);
            }
        }

        if (errored > 0)
        {
            _logger.LogWarning(
                "Environment drift check finished with {Errored} of {Total} projects unchecked.",
                errored, projectIds.Count);
        }
    }

    private async Task CheckProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var wiring = scope.ServiceProvider.GetRequiredService<IFrontendEnvironmentWiringService>();

        var driftResult = await wiring.SyncCrossProviderEnvironmentAsync(
            projectId,
            new EnvironmentSyncOptions(
                DetectDriftOnly: true,
                RunVerification: false,
                Source: "scheduled"),
            cancellationToken);

        if (!driftResult.DriftDetected || driftResult.Skipped)
        {
            return;
        }

        await wiring.SyncCrossProviderEnvironmentAsync(
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
