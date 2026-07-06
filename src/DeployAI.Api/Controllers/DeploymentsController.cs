using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeployAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class DeploymentsController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IDeploymentOrchestrator _orchestrator;

    public DeploymentsController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IDeploymentOrchestrator orchestrator)
    {
        _db = db;
        _currentUser = currentUser;
        _orchestrator = orchestrator;
    }

    [HttpPost("projects/{projectId:guid}/deployments")]
    public async Task<IActionResult> Trigger(Guid projectId, [FromBody] TriggerDeploymentRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that app." } });
        }

        var branch = string.IsNullOrWhiteSpace(request.Branch) ? project.DefaultBranch : request.Branch;
        var result = await _orchestrator.TriggerAsync(projectId, userId, branch, cancellationToken);

        return Accepted(new
        {
            deploymentId = result.DeploymentId,
            status = result.Status,
            targets = result.Targets.Select(t => new { providerName = t.ProviderName, status = t.Status })
        });
    }

    [HttpGet("projects/{projectId:guid}/deployments")]
    public async Task<IActionResult> ListForProject(Guid projectId, [FromQuery] int page = 1, [FromQuery] int perPage = 20, CancellationToken cancellationToken = default)
    {
        var userId = RequireUserId();
        var projectExists = await _db.Projects.AnyAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);
        if (!projectExists)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that app." } });
        }

        var query = _db.Deployments
            .Where(d => d.ProjectId == projectId)
            .Include(d => d.Targets)
            .OrderByDescending(d => d.CreatedAt);

        var total = await query.CountAsync(cancellationToken);
        var deployments = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            deployments = deployments.Select(MapDeploymentSummary),
            page,
            hasMore = page * perPage < total
        });
    }

    [HttpGet("deployments/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var deployment = await _db.Deployments
            .Include(d => d.Targets)
            .Include(d => d.Project)
            .FirstOrDefaultAsync(d => d.Id == id && d.Project.UserId == userId, cancellationToken);

        if (deployment is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that update." } });
        }

        return Ok(MapDeploymentDetail(deployment));
    }

    [HttpGet("deployments/{id:guid}/logs")]
    public async Task<IActionResult> GetLogs(Guid id, [FromQuery] Guid? target, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var deployment = await _db.Deployments
            .Include(d => d.Targets)
            .Include(d => d.Project)
            .FirstOrDefaultAsync(d => d.Id == id && d.Project.UserId == userId, cancellationToken);

        if (deployment is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that update." } });
        }

        var targetIds = deployment.Targets.Select(t => t.Id).ToList();
        if (target is not null)
        {
            targetIds = targetIds.Where(tid => tid == target.Value).ToList();
        }

        var logs = await _db.DeploymentLogs
            .Where(l => targetIds.Contains(l.DeploymentTargetId))
            .OrderBy(l => l.Sequence)
            .Join(_db.DeploymentTargets, l => l.DeploymentTargetId, t => t.Id, (l, t) => new
            {
                providerName = t.ProviderName,
                sequence = l.Sequence,
                line = l.Line,
                loggedAt = l.LoggedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(new { logs });
    }

    [HttpPost("deployments/{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var restoreService = HttpContext.RequestServices.GetRequiredService<IDeploymentRestoreService>();
        var result = await restoreService.RestorePreviousAsync(id, userId, cancellationToken);

        return Ok(new
        {
            deploymentId = result.DeploymentId,
            status = result.Status,
            targets = result.Targets.Select(t => new
            {
                providerName = t.ProviderName,
                status = t.Status,
                message = t.Message
            })
        });
    }

    private Guid RequireUserId()
    {
        return _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
    }

    private static object MapDeploymentSummary(Data.Entities.Deployment deployment)
    {
        var duration = deployment.CompletedAt.HasValue && deployment.StartedAt.HasValue
            ? (int?)(deployment.CompletedAt.Value - deployment.StartedAt.Value).TotalSeconds
            : null;

        return new
        {
            id = deployment.Id,
            branch = deployment.Branch,
            status = deployment.Status,
            durationSeconds = duration,
            startedAt = deployment.StartedAt,
            completedAt = deployment.CompletedAt,
            targets = deployment.Targets.Select(t => new
            {
                providerName = t.ProviderName,
                status = t.Status,
                deployUrl = t.DeployUrl
            })
        };
    }

    private static object MapDeploymentDetail(Data.Entities.Deployment deployment)
    {
        return new
        {
            id = deployment.Id,
            projectId = deployment.ProjectId,
            branch = deployment.Branch,
            status = deployment.Status,
            startedAt = deployment.StartedAt,
            completedAt = deployment.CompletedAt,
            targets = deployment.Targets.Select(t => new
            {
                id = t.Id,
                providerName = t.ProviderName,
                status = t.Status,
                deployUrl = t.DeployUrl,
                startedAt = t.StartedAt,
                completedAt = t.CompletedAt
            })
        };
    }

    public sealed record TriggerDeploymentRequest(string? Branch);
}
