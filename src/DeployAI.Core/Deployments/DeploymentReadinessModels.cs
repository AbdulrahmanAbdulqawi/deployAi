namespace DeployAI.Core.Deployments;

public enum DeploymentFileSeverity
{
    Blocking,
    Recommended,
    Warning
}

public sealed record MissingDeploymentFile(
    string Path,
    string Reason,
    DeploymentFileSeverity Severity);

public sealed record DeploymentReadinessResult(
    bool IsReady,
    string? CommitSha,
    bool UsesSplitOrigin,
    IReadOnlyList<MissingDeploymentFile> MissingFiles,
    IReadOnlyList<string> Warnings,
    string? WebsiteProviderName = null,
    string? ServerProviderName = null);

public sealed record DeploymentSetupResult(
    string BranchName,
    int PullRequestNumber,
    string PullRequestUrl,
    IReadOnlyList<string> CommittedFiles);

public sealed record GeneratedDeploymentFile(string Path, string Content);

public sealed record DeploymentSetupRequest(
    string GitRef,
    IReadOnlyList<DeploymentPlanPart> Parts,
    Guid? ProjectId = null,
    bool Regenerate = true,
    bool ForceRegenerate = false,
    bool? UseAi = null);

public sealed record DeploymentSetupMergeResult(
    bool Merged,
    string EnvSyncStatus,
    string? EnvSyncReason,
    IReadOnlyList<string> RailwayKeysApplied,
    IReadOnlyList<string> VercelKeysApplied);
