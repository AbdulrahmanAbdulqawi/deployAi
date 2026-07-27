using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace DeployAI.Api.Services;

/// <summary>Implements the fix PR flow, delegating actual fix generation to <see cref="IDeploymentFixGenerator"/> (Claude) and failure classification to <see cref="IDeploymentFailureAnalyzer"/>.</summary>
public sealed class DeploymentFixService : IDeploymentFixService
{
    private readonly DeployAIDbContext _db;
    private readonly IGitHubService _gitHubService;
    private readonly IDeploymentFixGenerator _fixGenerator;
    private readonly IDeploymentFailureAnalyzer _failureAnalyzer;
    private readonly IDeploymentVerificationService _verificationService;
    private readonly IEncryptionService _encryption;

    public DeploymentFixService(
        DeployAIDbContext db,
        IGitHubService gitHubService,
        IDeploymentFixGenerator fixGenerator,
        IDeploymentFailureAnalyzer failureAnalyzer,
        IDeploymentVerificationService verificationService,
        IEncryptionService encryption)
    {
        _db = db;
        _gitHubService = gitHubService;
        _fixGenerator = fixGenerator;
        _failureAnalyzer = failureAnalyzer;
        _verificationService = verificationService;
        _encryption = encryption;
    }

    public async Task<DeploymentFixResult> GenerateFixPullRequestAsync(
        Guid userId,
        Guid deploymentId,
        Guid deploymentTargetId,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken)
    {
        var deployment = await _db.Deployments
            .Include(d => d.Project)
            .Include(d => d.Targets)
            .ThenInclude(t => t.DeployTarget)
            .FirstOrDefaultAsync(d => d.Id == deploymentId && d.Project.UserId == userId, cancellationToken);

        if (deployment is null)
        {
            throw new DeployAIException("not_found", "We couldn't find that deployment.");
        }

        var target = deployment.Targets.FirstOrDefault(t => t.Id == deploymentTargetId);
        if (target is null)
        {
            throw new DeployAIException("not_found", "We couldn't find that deployment target.");
        }

        if (!string.Equals(target.Status, DeploymentStatuses.Failed, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeployAIException("fix_not_applicable", "Fixes can only be generated for failed deployment targets.");
        }

        var analysis = DeploymentFailureAnalysisJson.Parse(target.FailureAnalysisJson);
        if (analysis is null || !analysis.CanRequestClaudeFix)
        {
            throw new DeployAIException(
                "fix_not_applicable",
                "This failure does not look like a code or build error that Claude can fix.");
        }

        analysis = await RefreshFailureAnalysisAsync(target, analysis, cancellationToken);

        return await CreateFixPullRequestAsync(
            deployment,
            target,
            analysis,
            verificationContext: null,
            reportActivity,
            cancellationToken);
    }

    public async Task<DeploymentFixResult> GenerateVerificationFixPullRequestAsync(
        Guid userId,
        Guid deploymentId,
        string checkId,
        Guid? deploymentTargetId,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checkId))
        {
            throw new DeployAIException("invalid_request", "A health check id is required.");
        }

        var deployment = await _db.Deployments
            .Include(d => d.Project)
            .Include(d => d.Targets)
            .ThenInclude(t => t.DeployTarget)
            .FirstOrDefaultAsync(d => d.Id == deploymentId && d.Project.UserId == userId, cancellationToken);

        if (deployment is null)
        {
            throw new DeployAIException("not_found", "We couldn't find that deployment.");
        }

        var verification = await _verificationService.VerifyAsync(
            deploymentId,
            DeploymentVerificationScope.Both,
            cancellationToken);

        var check = verification.Checks.FirstOrDefault(c =>
            string.Equals(c.Id, checkId, StringComparison.OrdinalIgnoreCase));
        if (check is null)
        {
            throw new DeployAIException("check_not_found", "That health check was not found for this deployment.");
        }

        if (!check.CanRequestClaudeFix)
        {
            throw new DeployAIException(
                "fix_not_applicable",
                "This health check cannot be fixed with Claude.");
        }

        var target = ResolveVerificationFixTarget(deployment, check, deploymentTargetId);
        var websiteTarget = DeploymentTargetResolution.FindWebsiteTarget(deployment.Targets);
        var serverTarget = DeploymentTargetResolution.FindServerTarget(deployment.Targets);

        var verificationContext = new DeploymentVerificationFixContext(
            check.Id,
            check.Target,
            check.Label,
            check.Message,
            check.Url,
            websiteTarget?.DeployUrl,
            serverTarget?.DeployUrl);

        var excerpt = new StringBuilder()
            .AppendLine($"Health check: {check.Label} ({check.Id})")
            .AppendLine($"Status: {check.Status}")
            .AppendLine($"Message: {check.Message}");
        if (!string.IsNullOrWhiteSpace(check.Url))
        {
            excerpt.AppendLine($"URL: {check.Url}");
        }

        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            check.Message,
            excerpt.ToString().Trim(),
            check.ReferencedFiles,
            true);

        return await CreateFixPullRequestAsync(
            deployment,
            target,
            analysis,
            verificationContext,
            reportActivity,
            cancellationToken);
    }

    private static DeploymentTarget ResolveVerificationFixTarget(
        Deployment deployment,
        DeploymentVerificationCheck check,
        Guid? deploymentTargetId)
    {
        if (deploymentTargetId is not null)
        {
            var explicitTarget = deployment.Targets.FirstOrDefault(t => t.Id == deploymentTargetId.Value);
            if (explicitTarget is not null)
            {
                return explicitTarget;
            }
        }

        return check.Target switch
        {
            "server" => DeploymentTargetResolution.FindServerTarget(deployment.Targets)
                         ?? throw new DeployAIException("not_found", "We couldn't find the server target for this fix."),
            _ => DeploymentTargetResolution.FindWebsiteTarget(deployment.Targets)
                  ?? deployment.Targets.First()
        };
    }

    private async Task<DeploymentFixResult> CreateFixPullRequestAsync(
        Deployment deployment,
        DeploymentTarget target,
        DeploymentFailureAnalysis analysis,
        DeploymentVerificationFixContext? verificationContext,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken)
    {
        var repoParts = deployment.Project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (repoParts.Length != 2)
        {
            throw new DeployAIException("invalid_repository", "Invalid GitHub repository name.");
        }

        var userId = deployment.Project.UserId;
        var token = await GetGitHubTokenAsync(userId, cancellationToken);
        var gitRef = deployment.GitCommitSha ?? deployment.Branch;
        var targetConfig = DeployTargetConfig.Parse(target.DeployTarget.ConfigJson);

        var generated = await _fixGenerator.GenerateFixFilesAsync(
            deployment.ProjectId,
            repoParts[0],
            repoParts[1],
            gitRef,
            token,
            target.ProviderName,
            targetConfig.Framework,
            targetConfig,
            analysis,
            reportActivity,
            cancellationToken,
            verificationContext);

        if (generated is null || generated.Count == 0)
        {
            throw new DeployAIException("fix_generation_failed", "Could not generate fix files.");
        }

        var baseSha = await _gitHubService.GetBranchHeadShaAsync(
            token,
            repoParts[0],
            repoParts[1],
            deployment.Branch,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(baseSha))
        {
            baseSha = deployment.GitCommitSha;
        }

        if (string.IsNullOrWhiteSpace(baseSha))
        {
            throw new DeployAIException("github_ref_unavailable", "Could not resolve the Git reference for the fix branch.");
        }

        var branchPrefix = verificationContext is null
            ? $"deployai/fix-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"
            : $"deployai/verify-fix-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        string? createdBranch = null;
        var branchName = branchPrefix;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            branchName = attempt == 0 ? branchPrefix : $"{branchPrefix}-{attempt}";
            createdBranch = await _gitHubService.CreateBranchAsync(
                token,
                repoParts[0],
                repoParts[1],
                branchName,
                baseSha,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(createdBranch))
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(createdBranch))
        {
            throw new DeployAIException("fix_branch_failed", "Could not create a fix branch.");
        }

        var commitMessage = verificationContext is null
            ? "DeployAI: fix build errors from failed deployment"
            : $"DeployAI: fix health check {verificationContext.CheckId}";

        var commitSha = await _gitHubService.CommitFilesAsync(
            token,
            repoParts[0],
            repoParts[1],
            branchName,
            commitMessage,
            generated.Select(file => (file.Path, file.Content)).ToArray(),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(commitSha))
        {
            throw new DeployAIException("fix_commit_failed", "Could not commit generated fix files.");
        }

        var pullRequestTitle = verificationContext is null
            ? "DeployAI: fix build errors"
            : $"DeployAI: fix {verificationContext.Label}";

        var pullRequestBody = verificationContext is null
            ? $"Fixes a {target.ProviderName} build failure detected during deployment {deployment.Id}."
            : $"Fixes live deployment health check `{verificationContext.CheckId}` for deployment {deployment.Id}.";

        var pullRequest = await _gitHubService.CreatePullRequestAsync(
            token,
            repoParts[0],
            repoParts[1],
            pullRequestTitle,
            branchName,
            deployment.Branch,
            pullRequestBody,
            cancellationToken);

        if (pullRequest is null)
        {
            throw new DeployAIException("fix_pr_failed", "Could not open a pull request for the fix branch.");
        }

        return new DeploymentFixResult(
            branchName,
            pullRequest.Number,
            pullRequest.HtmlUrl,
            generated.Select(file => file.Path).ToArray());
    }

    public async Task MergeFixPullRequestAsync(
        Guid userId,
        string owner,
        string repo,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        _ = userId;
        var token = await GetGitHubTokenAsync(userId, cancellationToken);
        var merged = await _gitHubService.MergePullRequestAsync(
            token,
            owner,
            repo,
            pullRequestNumber,
            "DeployAI: merge build fix",
            cancellationToken);

        if (!merged)
        {
            throw new DeployAIException("fix_merge_failed", "Could not merge the fix pull request.");
        }
    }

    private async Task<DeploymentFailureAnalysis> RefreshFailureAnalysisAsync(
        DeploymentTarget target,
        DeploymentFailureAnalysis stored,
        CancellationToken cancellationToken)
    {
        var logLines = await _db.DeploymentLogs
            .Where(log => log.DeploymentTargetId == target.Id)
            .OrderBy(log => log.Sequence)
            .Select(log => log.Line)
            .ToListAsync(cancellationToken);

        if (logLines.Count == 0)
        {
            return stored;
        }

        var refreshed = _failureAnalyzer.AnalyzeForFix(target.ProviderName, logLines);
        if (!refreshed.CanRequestClaudeFix)
        {
            return stored;
        }

        target.FailureAnalysisJson = DeploymentFailureAnalysisJson.ToJson(refreshed);
        await _db.SaveChangesAsync(cancellationToken);
        return refreshed;
    }

    private async Task<string> GetGitHubTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken);
        return _encryption.Decrypt(user.GitHubTokenEncrypted);
    }
}
