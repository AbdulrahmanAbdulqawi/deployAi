namespace DeployAI.Core.Deployments;

/// <summary>Orchestrates the end-to-end fix flow: generating fix files via Claude, opening a PR, and merging it.</summary>
public interface IDeploymentFixService

{

    /// <summary>Generates and opens a PR fixing a failed deployment target.</summary>
    Task<DeploymentFixResult> GenerateFixPullRequestAsync(

        Guid userId,

        Guid deploymentId,

        Guid deploymentTargetId,

        Func<string, Task>? reportActivity,

        CancellationToken cancellationToken);



    /// <summary>Generates and opens a PR fixing one specific failed verification check.</summary>
    Task<DeploymentFixResult> GenerateVerificationFixPullRequestAsync(
        Guid userId,
        Guid deploymentId,
        string checkId,
        Guid? deploymentTargetId,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken);

    /// <summary>Merges a previously generated fix pull request.</summary>
    Task MergeFixPullRequestAsync(

        Guid userId,

        string owner,

        string repo,

        int pullRequestNumber,

        CancellationToken cancellationToken);

}


