namespace DeployAI.Core.Deployments;

/// <summary>How serious a missing split-origin setup file is - Blocking prevents readiness, the others are advisory.</summary>
public enum DeploymentFileSeverity
{
    Blocking,
    Recommended,
    Warning
}

/// <summary>A split-origin wiring file the repo is missing, why it's needed, and how serious the gap is.</summary>
public sealed record MissingDeploymentFile(
    string Path,
    string Reason,
    DeploymentFileSeverity Severity);

/// <summary>The result of scanning a repo/project for split-origin deployment readiness.</summary>
public sealed record DeploymentReadinessResult(
    bool IsReady,
    string? CommitSha,
    bool UsesSplitOrigin,
    IReadOnlyList<MissingDeploymentFile> MissingFiles,
    IReadOnlyList<string> Warnings,
    string? WebsiteProviderName = null,
    string? ServerProviderName = null,
    bool UsesSingleOriginCompose = false);

/// <summary>The pull request opened with generated deployment setup files.</summary>
public sealed record DeploymentSetupResult(
    string BranchName,
    int PullRequestNumber,
    string PullRequestUrl,
    IReadOnlyList<string> CommittedFiles);

/// <summary>A single generated setup file's path and content, before being committed.</summary>
public sealed record GeneratedDeploymentFile(string Path, string Content);

/// <summary>Input to generating deployment setup files: which ref/plan to generate them for, and whether to regenerate existing ones.</summary>
public sealed record DeploymentSetupRequest(
    string GitRef,
    IReadOnlyList<DeploymentPlanPart> Parts,
    Guid? ProjectId = null,
    bool Regenerate = true,
    bool ForceRegenerate = false,
    bool? UseAi = null);

/// <summary>The result of merging a deployment-setup pull request, including whether env sync ran and what it applied.</summary>
public sealed record DeploymentSetupMergeResult(
    bool Merged,
    string EnvSyncStatus,
    string? EnvSyncReason,
    IReadOnlyList<string> RailwayKeysApplied,
    IReadOnlyList<string> VercelKeysApplied);
