using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using DeployAI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/environment")]
public sealed class ProjectEnvironmentController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IFrontendEnvironmentWiringService _frontendEnvironmentWiring;

    public ProjectEnvironmentController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IFrontendEnvironmentWiringService frontendEnvironmentWiring)
    {
        _db = db;
        _currentUser = currentUser;
        _frontendEnvironmentWiring = frontendEnvironmentWiring;
    }

    [HttpGet("sync")]
    public async Task<IActionResult> GetSyncStatus(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await GetOwnedProjectAsync(projectId, cancellationToken);
        var state = ProjectEnvironmentSyncState.Parse(project.EnvironmentSyncJson);
        return Ok(MapSyncState(state));
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(
        Guid projectId,
        [FromQuery] bool redeployRailway = true,
        CancellationToken cancellationToken = default)
    {
        _ = await GetOwnedProjectAsync(projectId, cancellationToken);

        var result = await _frontendEnvironmentWiring.SyncCrossProviderEnvironmentAsync(
            projectId,
            new EnvironmentSyncOptions(
                RedeployRailwayAfterUpdate: redeployRailway,
                RedeployVercelAfterUpdate: false,
                EnsureWebsiteWiring: true,
                ApplyVercelEnv: true,
                ApplyRailwayEnv: true,
                RunVerification: true,
                Source: "manual"),
            cancellationToken);

        return Ok(MapResult(result));
    }

    private async Task<Data.Entities.Project> GetOwnedProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId
            ?? throw new DeployAIException("unauthorized", "Sign in to continue.");

        return await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken)
            ?? throw new DeployAIException("not_found", "We couldn't find that app.");
    }

    private static object MapSyncState(ProjectEnvironmentSyncState? state) =>
        state is null
            ? new { synced = false }
            : new
            {
                synced = true,
                lastSyncedAt = state.LastSyncedAt,
                source = state.Source,
                success = state.Success,
                driftDetected = state.DriftDetected,
                resolvedWebsiteUrl = state.ResolvedWebsiteUrl,
                resolvedApiUrl = state.ResolvedApiUrl,
                verificationMessages = state.VerificationMessages,
                driftDetails = state.DriftDetails
            };

    private static object MapResult(EnvironmentSyncResult result) => new
    {
        success = result.Success,
        skipped = result.Skipped,
        skipReason = result.SkipReason,
        driftDetected = result.DriftDetected,
        resolvedWebsiteUrl = result.ResolvedWebsiteUrl,
        resolvedApiUrl = result.ResolvedApiUrl,
        railwayKeysApplied = result.RailwayKeysApplied,
        vercelKeysApplied = result.VercelKeysApplied,
        verificationMessages = result.VerificationMessages,
        driftDetails = result.DriftDetails,
        source = result.Source,
        completedAt = result.CompletedAt
    };
}
