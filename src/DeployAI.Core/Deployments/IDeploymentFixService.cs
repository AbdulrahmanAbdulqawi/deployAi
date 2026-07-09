namespace DeployAI.Core.Deployments;



public interface IDeploymentFixService

{

    Task<DeploymentFixResult> GenerateFixPullRequestAsync(

        Guid userId,

        Guid deploymentId,

        Guid deploymentTargetId,

        Func<string, Task>? reportActivity,

        CancellationToken cancellationToken);



    Task<DeploymentFixResult> GenerateVerificationFixPullRequestAsync(
        Guid userId,
        Guid deploymentId,
        string checkId,
        Guid? deploymentTargetId,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken);

    Task MergeFixPullRequestAsync(

        Guid userId,

        string owner,

        string repo,

        int pullRequestNumber,

        CancellationToken cancellationToken);

}


