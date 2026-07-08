using System.Text.Json;
using System.Text.Json.Nodes;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

public sealed class DeploymentSetupService : IDeploymentSetupService
{
    private readonly DeployAIDbContext _db;
    private readonly IGitHubService _gitHubService;
    private readonly IDeploymentReadinessService _readinessService;
    private readonly IDeploymentFileGeneratorSelector _generatorSelector;
    private readonly IFrontendEnvironmentWiringService _environmentWiring;
    private readonly IEncryptionService _encryption;

    public DeploymentSetupService(
        DeployAIDbContext db,
        IGitHubService gitHubService,
        IDeploymentReadinessService readinessService,
        IDeploymentFileGeneratorSelector generatorSelector,
        IFrontendEnvironmentWiringService environmentWiring,
        IEncryptionService encryption)
    {
        _db = db;
        _gitHubService = gitHubService;
        _readinessService = readinessService;
        _generatorSelector = generatorSelector;
        _environmentWiring = environmentWiring;
        _encryption = encryption;
    }

    public async Task<DeploymentSetupResult> GenerateSetupBranchAsync(
        Guid userId,
        string owner,
        string repo,
        DeploymentSetupRequest request,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken)
    {
        var token = await GetGitHubTokenAsync(userId, cancellationToken);
        await ReportActivityAsync(reportActivity, "Scanning repository for split-origin readiness…");
        var readiness = await _readinessService.ScanRepositoryAsync(
            token,
            owner,
            repo,
            request.GitRef,
            request.Parts,
            cancellationToken);

        var missing = readiness.MissingFiles
            .Where(file => file.Severity == DeploymentFileSeverity.Blocking ||
                           file.Severity == DeploymentFileSeverity.Recommended)
            .ToArray();
        if (missing.Length == 0 && request.ForceRegenerate)
        {
            missing = SplitOriginReadinessEvaluator.BuildRegenerationTargets(request.Parts).ToArray();
        }

        if (missing.Length == 0)
        {
            throw new DeployAIException("setup_not_needed", "Repository already has required split-origin deployment files.");
        }

        var selection = await _generatorSelector.SelectAsync(request.UseAi, reportActivity);
        await PersistAiPreferenceAsync(userId, request, selection.Mode, cancellationToken);

        await ReportActivityAsync(
            reportActivity,
            selection.Mode == DeploymentFileGeneratorSelector.AiMode
                ? $"Found {missing.Length} deployment target(s) for Claude to generate or fix."
                : $"Found {missing.Length} deployment target(s) to generate from built-in templates.");

        var generated = await selection.Generator.GenerateMissingFilesAsync(
            owner,
            repo,
            request.GitRef,
            token,
            request.Parts,
            missing,
            reportActivity,
            cancellationToken);
        if (generated.Count == 0)
        {
            throw new DeployAIException("setup_generation_failed", "Could not generate deployment files.");
        }

        var baseSha = readiness.CommitSha ??
            await _gitHubService.GetBranchHeadShaAsync(token, owner, repo, request.GitRef, cancellationToken);
        if (string.IsNullOrWhiteSpace(baseSha))
        {
            throw new DeployAIException("github_ref_unavailable", "Could not resolve the Git reference for setup.");
        }

        var branchName = $"deployai/setup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        await ReportActivityAsync(reportActivity, "Creating setup branch on GitHub…");
        string? createdBranch = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = attempt == 0 ? branchName : $"{branchName}-{attempt}";
            createdBranch = await _gitHubService.CreateBranchAsync(token, owner, repo, candidate, baseSha, cancellationToken);
            if (!string.IsNullOrWhiteSpace(createdBranch))
            {
                branchName = candidate;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(createdBranch))
        {
            throw new DeployAIException("setup_branch_failed", "Could not create a setup branch.");
        }

        await ReportActivityAsync(reportActivity, $"Committing {generated.Count} file(s) to {branchName}…");
        var commitSha = await _gitHubService.CommitFilesAsync(
            token,
            owner,
            repo,
            branchName,
            "DeployAI: add split-origin deployment files for Vercel + Railway",
            generated.Select(file => (file.Path, file.Content)).ToArray(),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(commitSha))
        {
            throw new DeployAIException("setup_commit_failed", "Could not commit generated deployment files.");
        }

        var defaultBranch = await ResolveDefaultBranchAsync(token, owner, repo, request.GitRef, cancellationToken);
        await ReportActivityAsync(reportActivity, "Opening pull request…");
        var pullRequest = await _gitHubService.CreatePullRequestAsync(
            token,
            owner,
            repo,
            "DeployAI: split-origin deployment setup",
            branchName,
            defaultBranch,
            "Adds SPA-only vercel.json, write-api-env.mjs, api-base interceptor, railway.toml, CORS, and health endpoint for Vercel + Railway split-origin deployment.",
            cancellationToken);

        if (pullRequest is null)
        {
            throw new DeployAIException("setup_pr_failed", "Could not open a pull request for the setup branch.");
        }

        return new DeploymentSetupResult(
            branchName,
            pullRequest.Number,
            pullRequest.HtmlUrl,
            generated.Select(file => file.Path).ToArray());
    }

    private static Task ReportActivityAsync(Func<string, Task>? reportActivity, string message) =>
        reportActivity is null ? Task.CompletedTask : reportActivity(message);

    public async Task<DeploymentSetupMergeResult> MergeSetupPullRequestAsync(
        Guid userId,
        string owner,
        string repo,
        int pullRequestNumber,
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        var token = await GetGitHubTokenAsync(userId, cancellationToken);
        var merged = await _gitHubService.MergePullRequestAsync(
            token,
            owner,
            repo,
            pullRequestNumber,
            "DeployAI: merge split-origin deployment setup",
            cancellationToken);

        if (!merged)
        {
            throw new DeployAIException("setup_merge_failed", "Could not merge the setup pull request.");
        }

        if (projectId is null)
        {
            return new DeploymentSetupMergeResult(
                true,
                "skipped",
                "No project context was provided for environment sync.",
                [],
                []);
        }

        var projectExists = await _db.Projects.AnyAsync(
            p => p.Id == projectId.Value && p.UserId == userId,
            cancellationToken);
        if (!projectExists)
        {
            return new DeploymentSetupMergeResult(
                true,
                "skipped",
                "Project not found for environment sync.",
                [],
                []);
        }

        var syncResult = await _environmentWiring.SyncCrossProviderEnvironmentAsync(
            projectId.Value,
            new EnvironmentSyncOptions(
                RedeployRailwayAfterUpdate: false,
                RedeployVercelAfterUpdate: false,
                EnsureWebsiteWiring: false,
                ApplyVercelEnv: true,
                ApplyRailwayEnv: true,
                RunVerification: false,
                Source: "deployment-setup"),
            cancellationToken);

        if (syncResult.Skipped)
        {
            return new DeploymentSetupMergeResult(
                true,
                "pending",
                syncResult.SkipReason ?? "Deploy URLs are not available yet. Environment variables will sync after the first successful deploy.",
                [],
                []);
        }

        return new DeploymentSetupMergeResult(
            true,
            syncResult.Success ? "completed" : "pending",
            syncResult.Success ? null : "Environment sync did not complete successfully.",
            syncResult.RailwayKeysApplied,
            syncResult.VercelKeysApplied);
    }

    public async Task UseSetupBranchAsync(
        Guid userId,
        Guid projectId,
        string branch,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);
        if (project is null)
        {
            throw new DeployAIException("project_not_found", "We couldn't find that app.");
        }

        project.DefaultBranch = branch;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        var state = ParseSetupState(project.DeploymentSetupJson);
        state["setupBranch"] = branch;
        state["setupCompletedAt"] = DateTimeOffset.UtcNow.ToString("O");
        project.DeploymentSetupJson = state.ToJsonString();
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool?> GetAiSetupPreferenceAsync(
        Guid userId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(
            p => p.Id == projectId && p.UserId == userId,
            cancellationToken);
        if (project is null)
        {
            throw new DeployAIException("project_not_found", "We couldn't find that app.");
        }

        var state = ParseSetupState(project.DeploymentSetupJson);
        return state["aiSetupEnabled"]?.GetValue<bool>();
    }

    public async Task SetAiSetupPreferenceAsync(
        Guid userId,
        Guid projectId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(
            p => p.Id == projectId && p.UserId == userId,
            cancellationToken);
        if (project is null)
        {
            throw new DeployAIException("project_not_found", "We couldn't find that app.");
        }

        var state = ParseSetupState(project.DeploymentSetupJson);
        state["aiSetupEnabled"] = enabled;
        project.DeploymentSetupJson = state.ToJsonString();
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task PersistAiPreferenceAsync(
        Guid userId,
        DeploymentSetupRequest request,
        string mode,
        CancellationToken cancellationToken)
    {
        // Only remember an explicit user choice, not a fallback outcome.
        if (request.ProjectId is not { } projectId || request.UseAi is not { } useAi)
        {
            return;
        }

        _ = mode;
        await SetAiSetupPreferenceAsync(userId, projectId, useAi, cancellationToken);
    }

    private static JsonObject ParseSetupState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private async Task<string> GetGitHubTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken);
        return _encryption.Decrypt(user.GitHubTokenEncrypted);
    }

    private async Task<string> ResolveDefaultBranchAsync(
        string token,
        string owner,
        string repo,
        string gitRef,
        CancellationToken cancellationToken)
    {
        if (!gitRef.Contains('/', StringComparison.Ordinal))
        {
            return gitRef;
        }

        var repository = await _gitHubService.GetRepositoryAsync(token, owner, repo, cancellationToken);
        return string.IsNullOrWhiteSpace(repository?.DefaultBranch) ? "main" : repository.DefaultBranch;
    }
}
