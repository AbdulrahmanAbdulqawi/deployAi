namespace DeployAI.Core.Deployments;

/// <summary>Switches a project's default branch and, optionally, triggers a deployment from it.</summary>
public interface IProjectBranchDeployService
{
    /// <summary>Switches the project's default branch, deploying it immediately if requested.</summary>
    Task<UseBranchDeployResult> UseBranchAndDeployAsync(
        Guid userId,
        Guid projectId,
        string branch,
        bool triggerFullDeploy,
        CancellationToken cancellationToken);
}
