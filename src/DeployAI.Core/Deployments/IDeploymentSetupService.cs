namespace DeployAI.Core.Deployments;

public interface IDeploymentSetupService
{
    Task<DeploymentSetupResult> GenerateSetupBranchAsync(
        Guid userId,
        string owner,
        string repo,
        DeploymentSetupRequest request,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken);

    Task<DeploymentSetupMergeResult> MergeSetupPullRequestAsync(
        Guid userId,
        string owner,
        string repo,
        int pullRequestNumber,
        Guid? projectId,
        CancellationToken cancellationToken);

    Task UseSetupBranchAsync(
        Guid userId,
        Guid projectId,
        string branch,
        CancellationToken cancellationToken);
}
