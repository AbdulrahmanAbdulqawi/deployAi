using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Controllers;

/// <summary>
/// One place to see whether every deployed project still works.
/// </summary>
/// <remarks>
/// The per-project health blob already existed but only ever answered "how is this one project",
/// one project at a time, with no history and no way to tell a failing check from an unchecked one.
/// Everything here reads the recorded verification state rather than probing live, so opening the
/// page costs no provider API calls.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/fleet")]
public sealed class FleetController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IFleetVerificationService _fleet;
    private readonly IBackgroundJobClient _jobs;

    public FleetController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IFleetVerificationService fleet,
        IBackgroundJobClient jobs)
    {
        _db = db;
        _currentUser = currentUser;
        _fleet = fleet;
        _jobs = jobs;
    }

    /// <summary>Every project the caller owns, with the current state of each of its checks.</summary>
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var userId = RequireUserId();

        var projects = await _db.Projects
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.Id, p.Name, p.LogoKey, p.HealthJson })
            .ToListAsync(cancellationToken);

        var projectIds = projects.Select(p => p.Id).ToList();

        // One indexed scan over the current picture — the reason ProjectCheckState exists beside the
        // append-only history rather than being derived from it on every page load.
        var states = await _db.ProjectCheckStates
            .AsNoTracking()
            .Where(s => projectIds.Contains(s.ProjectId))
            .ToListAsync(cancellationToken);

        var byProject = states.ToLookup(s => s.ProjectId);

        var lastSweep = await _db.ProjectVerificationRuns
            .AsNoTracking()
            .Where(r => projectIds.Contains(r.ProjectId))
            .OrderByDescending(r => r.CompletedAt)
            .Select(r => (DateTimeOffset?)r.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var rows = projects.Select(p =>
        {
            var health = ProjectHealthState.Parse(p.HealthJson);
            var checks = byProject[p.Id].OrderBy(s => s.Target).ThenBy(s => s.CheckId).ToList();

            return new
            {
                projectId = p.Id,
                name = p.Name,
                logoKey = p.LogoKey,
                status = StatusName(health?.Status ?? ProjectHealthStatus.Unknown),
                lastCheckedAt = health?.LastCheckedAt,
                summary = health?.Summary,
                passed = Count(checks, VerificationCheckStatus.Passed),
                failed = Count(checks, VerificationCheckStatus.Failed),
                warning = Count(checks, VerificationCheckStatus.Warning),
                inconclusive = Count(checks, VerificationCheckStatus.Inconclusive),
                skipped = Count(checks, VerificationCheckStatus.Skipped),
                checks = checks.Select(MapCheck)
            };
        }).ToList();

        return Ok(new
        {
            lastSweepAt = lastSweep,
            projects = rows
        });
    }

    /// <summary>How one check on one project has behaved over time.</summary>
    [HttpGet("projects/{projectId:guid}/history")]
    public async Task<IActionResult> History(
        Guid projectId,
        [FromQuery] string? checkId,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        await RequireOwnedProjectAsync(projectId, cancellationToken);

        var results = _db.ProjectVerificationCheckResults
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId);

        if (!string.IsNullOrWhiteSpace(checkId))
        {
            results = results.Where(r => r.CheckId == checkId);
        }

        var history = await results
            .OrderByDescending(r => r.ObservedAt)
            .Take(Math.Clamp(limit == 0 ? 50 : limit, 1, 500))
            .Select(r => new
            {
                r.CheckId,
                r.Label,
                status = StatusName(r.Status),
                r.Message,
                r.ObservedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(new { projectId, checkId, history });
    }

    /// <summary>
    /// Re-checks everything the caller owns, now.
    /// </summary>
    /// <remarks>
    /// The reason the sweep is not only a schedule: waiting an hour to find out whether a fix worked
    /// makes the fix untestable. Enqueued rather than run inline because a fleet of any size takes
    /// longer than a request should.
    /// </remarks>
    [HttpPost("sweep")]
    public IActionResult Sweep()
    {
        var userId = RequireUserId();
        var jobId = _jobs.Enqueue<ProjectHealthMonitorJob>(
            job => job.RunForUserAsync(userId, CancellationToken.None));

        return Accepted(new { jobId, scope = "user" });
    }

    /// <summary>Re-checks one project and returns what was found, for a "check now" button.</summary>
    [HttpPost("projects/{projectId:guid}/verify")]
    public async Task<IActionResult> VerifyProject(Guid projectId, CancellationToken cancellationToken)
    {
        await RequireOwnedProjectAsync(projectId, cancellationToken);

        var run = await _fleet.VerifyOneAsync(
            projectId, VerificationRunTriggers.Manual, cancellationToken);

        return Ok(new
        {
            runId = run.RunId,
            status = StatusName(run.Outcome),
            summary = run.Summary
        });
    }

    private static object MapCheck(ProjectCheckState state) => new
    {
        checkId = state.CheckId,
        label = state.Label,
        target = state.Target,
        status = StatusName(state.Status),
        message = state.Message,
        url = state.Url,
        suggestedAction = state.SuggestedAction,
        // The pair that makes an alert honest: what was last actually concluded, and when it changed.
        // A check failing now but last conclusive a week ago is a different situation from one that
        // broke this morning.
        lastConclusiveStatus = state.LastConclusiveStatus is { } s ? StatusName(s) : null,
        lastConclusiveAt = state.LastConclusiveAt,
        statusChangedAt = state.StatusChangedAt,
        lastObservedAt = state.LastObservedAt,
        consecutiveFailures = state.ConsecutiveFailures,
        consecutiveInconclusive = state.ConsecutiveInconclusive,
        deployTargetId = state.DeployTargetId
    };

    /// <summary>
    /// Lowercased on the way out, matching the convention the client already reads for project
    /// status and verification checks.
    /// </summary>
    private static string StatusName(VerificationCheckStatus status) =>
        status.ToString().ToLowerInvariant();

    private static string StatusName(ProjectHealthStatus status) =>
        status.ToString().ToLowerInvariant();

    private static int Count(IEnumerable<ProjectCheckState> checks, VerificationCheckStatus status) =>
        checks.Count(c => c.Status == status);

    private async Task RequireOwnedProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var owned = await _db.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);

        if (!owned)
        {
            throw new DeployAIException("project_not_found", "That project could not be found.");
        }
    }

    private Guid RequireUserId() =>
        _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
}
