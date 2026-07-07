using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Security;
using DeployAI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DeployAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/github/repos/{owner}/{repo}")]
public sealed class DeploymentSetupController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IDeploymentReadinessService _readinessService;
    private readonly IDeploymentSetupService _setupService;
    private readonly IDeploymentFixService _fixService;
    private readonly IEncryptionService _encryption;

    public DeploymentSetupController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IDeploymentReadinessService readinessService,
        IDeploymentSetupService setupService,
        IDeploymentFixService fixService,
        IEncryptionService encryption)
    {
        _db = db;
        _currentUser = currentUser;
        _readinessService = readinessService;
        _setupService = setupService;
        _fixService = fixService;
        _encryption = encryption;
    }

    [HttpPost("deployment-readiness")]
    public async Task<IActionResult> ScanDeploymentReadiness(
        string owner,
        string repo,
        [FromBody] ScanReadinessRequest request,
        CancellationToken cancellationToken)
    {
        var token = await GetGitHubTokenAsync(cancellationToken);
        var result = await _readinessService.ScanRepositoryAsync(
            token,
            owner,
            repo,
            request.Ref,
            request.Parts,
            cancellationToken);
        return Ok(MapReadiness(result));
    }

    [HttpGet("~/api/projects/{projectId:guid}/deployment-readiness")]
    public async Task<IActionResult> GetProjectDeploymentReadiness(
        Guid projectId,
        [FromQuery] string? @ref,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var exists = await _db.Projects.AnyAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);
        if (!exists)
        {
            return NotFound();
        }

        var result = await _readinessService.ScanProjectAsync(projectId, @ref, cancellationToken);
        return Ok(MapReadiness(result));
    }

    [HttpPost("deployment-setup")]
    [RequestTimeout("claude-agent")]
    public async Task CreateDeploymentSetup(
        string owner,
        string repo,
        [FromBody] DeploymentSetupRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

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
            var result = await _setupService.GenerateSetupBranchAsync(
                userId,
                owner,
                repo,
                request,
                ReportActivity,
                cancellationToken);

            await WriteStreamEventAsync(
                new
                {
                    type = "complete",
                    branchName = result.BranchName,
                    pullRequestNumber = result.PullRequestNumber,
                    pullRequestUrl = result.PullRequestUrl,
                    committedFiles = result.CommittedFiles
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

    [HttpPost("deployment-setup/merge")]
    public async Task<IActionResult> MergeDeploymentSetup(
        string owner,
        string repo,
        [FromBody] MergeSetupRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var result = await _setupService.MergeSetupPullRequestAsync(
            userId,
            owner,
            repo,
            request.PullRequestNumber,
            request.ProjectId,
            cancellationToken);
        return Ok(new
        {
            merged = result.Merged,
            envSync = result.EnvSyncStatus,
            envSyncReason = result.EnvSyncReason,
            railwayKeysApplied = result.RailwayKeysApplied,
            vercelKeysApplied = result.VercelKeysApplied
        });
    }

    [HttpPost("deployment-fix/merge")]
    public async Task<IActionResult> MergeDeploymentFix(
        string owner,
        string repo,
        [FromBody] MergeSetupRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        await _fixService.MergeFixPullRequestAsync(userId, owner, repo, request.PullRequestNumber, cancellationToken);
        return Ok(new { merged = true });
    }

    [HttpPost("~/api/projects/{projectId:guid}/deployment-setup/use-branch")]
    public async Task<IActionResult> UseSetupBranch(
        Guid projectId,
        [FromBody] UseSetupBranchRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        await _setupService.UseSetupBranchAsync(userId, projectId, request.Branch, cancellationToken);
        return Ok(new { branch = request.Branch });
    }

    private static object MapReadiness(DeploymentReadinessResult result) => new
    {
        isReady = result.IsReady,
        commitSha = result.CommitSha,
        usesSplitOrigin = result.UsesSplitOrigin,
        missingFiles = result.MissingFiles.Select(file => new
        {
            path = file.Path,
            reason = file.Reason,
            severity = file.Severity.ToString().ToLowerInvariant()
        }),
        warnings = result.Warnings
    };

    private Guid RequireUserId() =>
        _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");

    private async Task<string> GetGitHubTokenAsync(CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken);
        return _encryption.Decrypt(user.GitHubTokenEncrypted);
    }

    public sealed record MergeSetupRequest(int PullRequestNumber, Guid? ProjectId = null);
    public sealed record UseSetupBranchRequest(string Branch);
    public sealed record ScanReadinessRequest(string Ref, IReadOnlyList<DeploymentPlanPart> Parts);
}
