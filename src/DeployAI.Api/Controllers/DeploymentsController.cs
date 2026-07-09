using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace DeployAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class DeploymentsController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IDeploymentOrchestrator _orchestrator;
    private readonly IDeploymentFixService _fixService;
    private readonly IDeploymentVerificationService _deploymentVerification;

    public DeploymentsController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IDeploymentOrchestrator orchestrator,
        IDeploymentFixService fixService,
        IDeploymentVerificationService deploymentVerification)
    {
        _db = db;
        _currentUser = currentUser;
        _orchestrator = orchestrator;
        _fixService = fixService;
        _deploymentVerification = deploymentVerification;
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

    [HttpPost("projects/{projectId:guid}/deployments/targets/{deployTargetId:guid}")]
    public async Task<IActionResult> TriggerTarget(
        Guid projectId,
        Guid deployTargetId,
        [FromBody] TriggerDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that app." } });
        }

        var branch = string.IsNullOrWhiteSpace(request.Branch) ? project.DefaultBranch : request.Branch;
        var result = await _orchestrator.TriggerTargetAsync(projectId, userId, deployTargetId, branch, cancellationToken);

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

    [HttpPost("deployments/{id:guid}/verify")]
    public async Task<IActionResult> Verify(
        Guid id,
        [FromQuery] string scope = "both",
        CancellationToken cancellationToken = default)
    {
        var userId = RequireUserId();
        var deployment = await _db.Deployments
            .Include(d => d.Project)
            .FirstOrDefaultAsync(d => d.Id == id && d.Project.UserId == userId, cancellationToken);

        if (deployment is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that update." } });
        }

        if (!Enum.TryParse<DeploymentVerificationScope>(scope, ignoreCase: true, out var parsedScope))
        {
            return BadRequest(new { error = new { code = "invalid_scope", message = "Scope must be website, server, or both." } });
        }

        var result = await _deploymentVerification.VerifyAsync(id, parsedScope, cancellationToken);
        return Ok(new
        {
            success = result.Success,
            scope = result.Scope,
            completedAt = result.CompletedAt,
            checks = result.Checks.Select(check => new
            {
                id = check.Id,
                target = check.Target,
                label = check.Label,
                status = check.Status,
                message = check.Message,
                url = check.Url,
                suggestedAction = check.SuggestedAction,
                canRequestClaudeFix = check.CanRequestClaudeFix,
                referencedFiles = check.ReferencedFiles
            })
        });
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

    [HttpPost("deployments/{id:guid}/targets/{targetId:guid}/fix")]
    [RequestTimeout("claude-agent")]
    public async Task GenerateFix(
        Guid id,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        var startedAt = DateTimeOffset.UtcNow;
        await WriteStreamEventAsync(new { type = "started", startedAt }, cancellationToken);

        async Task ReportActivity(string message)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await WriteStreamEventAsync(new { type = "log", message }, cancellationToken);
        }

        try
        {
            var result = await _fixService.GenerateFixPullRequestAsync(
                userId,
                id,
                targetId,
                ReportActivity,
                cancellationToken);

            var durationSeconds = (int)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
            await WriteStreamEventAsync(
                new
                {
                    type = "complete",
                    branchName = result.BranchName,
                    pullRequestNumber = result.PullRequestNumber,
                    pullRequestUrl = result.PullRequestUrl,
                    committedFiles = result.CommittedFiles,
                    durationSeconds
                },
                cancellationToken);
        }
        catch (DeployAIException ex)
        {
            await WriteStreamEventAsync(new { type = "error", code = ex.ErrorCode, message = ex.Message }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await WriteStreamEventAsync(
                new
                {
                    type = "error",
                    code = "claude_request_timeout",
                    message = "The request was canceled or timed out. Try again — large repositories can take several minutes."
                },
                CancellationToken.None);
        }
    }

    [HttpPost("deployments/{id:guid}/verification-fix")]
    [RequestTimeout("claude-agent")]
    public async Task GenerateVerificationFix(
        Guid id,
        [FromBody] VerificationFixRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        var startedAt = DateTimeOffset.UtcNow;
        await WriteStreamEventAsync(new { type = "started", startedAt }, cancellationToken);

        async Task ReportActivity(string message)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await WriteStreamEventAsync(new { type = "log", message }, cancellationToken);
        }

        try
        {
            var result = await _fixService.GenerateVerificationFixPullRequestAsync(
                userId,
                id,
                request.CheckId,
                request.TargetId,
                ReportActivity,
                cancellationToken);

            var durationSeconds = (int)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
            await WriteStreamEventAsync(
                new
                {
                    type = "complete",
                    branchName = result.BranchName,
                    pullRequestNumber = result.PullRequestNumber,
                    pullRequestUrl = result.PullRequestUrl,
                    committedFiles = result.CommittedFiles,
                    durationSeconds
                },
                cancellationToken);
        }
        catch (DeployAIException ex)
        {
            await WriteStreamEventAsync(new { type = "error", code = ex.ErrorCode, message = ex.Message }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await WriteStreamEventAsync(
                new
                {
                    type = "error",
                    code = "claude_request_timeout",
                    message = "The request was canceled or timed out. Try again — large repositories can take several minutes."
                },
                CancellationToken.None);
        }
    }

    private async Task WriteStreamEventAsync(object payload, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(payload) + "\n";
        await Response.WriteAsync(line, cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
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
            gitCommitSha = deployment.GitCommitSha,
            gitCommitMessage = deployment.GitCommitMessage,
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
        var duration = deployment.CompletedAt.HasValue && deployment.StartedAt.HasValue
            ? (int?)(deployment.CompletedAt.Value - deployment.StartedAt.Value).TotalSeconds
            : null;

        return new
        {
            id = deployment.Id,
            projectId = deployment.ProjectId,
            branch = deployment.Branch,
            gitCommitSha = deployment.GitCommitSha,
            gitCommitMessage = deployment.GitCommitMessage,
            status = deployment.Status,
            startedAt = deployment.StartedAt,
            completedAt = deployment.CompletedAt,
            durationSeconds = duration,
            targets = deployment.Targets.Select(t =>
            {
                var analysis = DeploymentFailureAnalysisJson.Parse(t.FailureAnalysisJson);
                return new
                {
                    id = t.Id,
                    deployTargetId = t.DeployTargetId,
                    providerName = t.ProviderName,
                    status = t.Status,
                    deployUrl = t.DeployUrl,
                    startedAt = t.StartedAt,
                    completedAt = t.CompletedAt,
                    failureAnalysis = analysis is null
                        ? null
                        : new
                        {
                            category = analysis.Category switch
                            {
                                DeploymentFailureCategory.CodeBuild => "code_build",
                                DeploymentFailureCategory.Infrastructure => "infrastructure",
                                _ => "unknown"
                            },
                            summary = analysis.Summary,
                            errorExcerpt = analysis.ErrorExcerpt,
                            referencedFiles = analysis.ReferencedFiles,
                            errorCount = analysis.ErrorCount,
                            canRequestClaudeFix = analysis.CanRequestClaudeFix
                        }
                };
            })
        };
    }

    public sealed record TriggerDeploymentRequest(string? Branch);

    public sealed record VerificationFixRequest(string CheckId, Guid? TargetId);
}
