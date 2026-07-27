namespace DeployAI.Core.Deployments;

/// <summary>Generates, merges, and adopts the deployment setup files/branches needed to make a repo deployable.</summary>
public interface IDeploymentSetupService
{
    /// <summary>Generates deployment setup files (via Claude) and opens a PR with them.</summary>
    Task<DeploymentSetupResult> GenerateSetupBranchAsync(
        Guid userId,
        string owner,
        string repo,
        DeploymentSetupRequest request,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken);

    /// <summary>Merges a generated setup pull request, then re-syncs env wiring for the linked project if one is specified.</summary>
    Task<DeploymentSetupMergeResult> MergeSetupPullRequestAsync(
        Guid userId,
        string owner,
        string repo,
        int pullRequestNumber,
        Guid? projectId,
        CancellationToken cancellationToken);

    /// <summary>Switches a project to deploy from an existing branch instead of generating new setup files.</summary>
    Task UseSetupBranchAsync(
        Guid userId,
        Guid projectId,
        string branch,
        CancellationToken cancellationToken);

    /// <summary>Gets whether AI-generated setup is enabled for a project, or null if never set.</summary>
    Task<bool?> GetAiSetupPreferenceAsync(
        Guid userId,
        Guid projectId,
        CancellationToken cancellationToken);

    /// <summary>Enables or disables AI-generated setup for a project.</summary>
    Task SetAiSetupPreferenceAsync(
        Guid userId,
        Guid projectId,
        bool enabled,
        CancellationToken cancellationToken);
}
