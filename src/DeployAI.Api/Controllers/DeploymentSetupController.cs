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

/// <summary>
/// Scans a repo/project for split-origin deployment readiness, and generates (via Claude), merges,
/// and adopts the setup files/PRs needed to make a repo deployable - plus the related AI-setup
/// preference and "use this branch" project settings.
/// </summary>
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

    /// <summary>
    /// Scans a repo at a given ref for split-origin deployment readiness (missing wiring files,
    /// warnings) against a caller-supplied deployment plan, without requiring an existing project.
    /// </summary>
    /// <param name="owner">Repo owner/org.</param>
    /// <param name="repo">Repo name.</param>
    /// <param name="request">Git ref to scan and the deployment plan parts to evaluate against.</param>
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

    /// <summary>
    /// Scans an existing project's linked repo/targets for split-origin deployment readiness.
    /// </summary>
    /// <param name="projectId">The project to scan (owned by the current user).</param>
    /// <param name="ref">Branch, tag, or commit SHA; null uses the project's default branch.</param>
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

    /// <summary>
    /// Generates the deployment setup files for a repo using Claude and opens a PR with them,
    /// streaming progress as newline-delimited JSON (<c>started</c>/<c>log</c>/<c>complete</c>/
    /// <c>error</c> events) rather than a single response, since generation can take minutes.
    /// </summary>
    /// <param name="owner">Repo owner/org.</param>
    /// <param name="repo">Repo name.</param>
    /// <param name="request">Git ref and deployment plan parts to generate setup files for.</param>
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
            var result = await _setupService.GenerateSetupBranchAsync(
                userId,
                owner,
                repo,
                request,
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

    /// <summary>
    /// Merges a previously generated deployment-setup pull request, then re-syncs cross-provider
    /// environment wiring for the linked project if one is specified.
    /// </summary>
    /// <param name="owner">Repo owner/org.</param>
    /// <param name="repo">Repo name.</param>
    /// <param name="request">The PR number to merge and, optionally, the project to re-sync env wiring for.</param>
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

    /// <summary>Merges a previously generated deployment-fix pull request (e.g. a build/wiring fix).</summary>
    /// <param name="owner">Repo owner/org.</param>
    /// <param name="repo">Repo name.</param>
    /// <param name="request">The PR number to merge.</param>
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

    /// <summary>Gets whether AI-generated deployment setup is enabled for a project.</summary>
    /// <param name="projectId">The project to check.</param>
    [HttpGet("~/api/projects/{projectId:guid}/settings/ai-setup")]
    public async Task<IActionResult> GetAiSetupPreference(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var enabled = await _setupService.GetAiSetupPreferenceAsync(userId, projectId, cancellationToken);
        return Ok(new { enabled });
    }

    /// <summary>Enables or disables AI-generated deployment setup for a project.</summary>
    /// <param name="projectId">The project to update.</param>
    /// <param name="request">The new preference value.</param>
    [HttpPut("~/api/projects/{projectId:guid}/settings/ai-setup")]
    public async Task<IActionResult> SetAiSetupPreference(
        Guid projectId,
        [FromBody] AiSetupPreferenceRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        await _setupService.SetAiSetupPreferenceAsync(userId, projectId, request.Enabled, cancellationToken);
        return Ok(new { enabled = request.Enabled });
    }

    /// <summary>
    /// Switches a project to deploy from a different existing branch (e.g. one containing manually
    /// authored setup files, as an alternative to the AI-generated setup flow).
    /// </summary>
    /// <param name="projectId">The project to update.</param>
    /// <param name="request">The branch to switch to.</param>
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

    /// <summary>
    /// The readiness scan as the wizard receives it. Hand-written, so a field the scan computes is
    /// only delivered if it is also listed here — see <c>usesSingleOriginCompose</c> below.
    /// </summary>
    internal static object MapReadiness(DeploymentReadinessResult result) => new
    {
        isReady = result.IsReady,
        commitSha = result.CommitSha,
        usesSplitOrigin = result.UsesSplitOrigin,
        // Computed on every scan and dropped here, which is why the wizard could evaluate a compose
        // repository, mark its missing compose file blocking, and then render nothing: the one field
        // that says which shape the findings belong to never left the API.
        usesSingleOriginCompose = result.UsesSingleOriginCompose,
        websiteProviderName = result.WebsiteProviderName,
        serverProviderName = result.ServerProviderName,
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
    public sealed record AiSetupPreferenceRequest(bool Enabled);
    public sealed record ScanReadinessRequest(string Ref, IReadOnlyList<DeploymentPlanPart> Parts);
}
