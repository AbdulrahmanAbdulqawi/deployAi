namespace DeployAI.Core.Deployments;

public interface IProjectBranchDeployService
{
    Task<UseBranchDeployResult> UseBranchAndDeployAsync(
        Guid userId,
        Guid projectId,
        string branch,
        bool triggerFullDeploy,
        CancellationToken cancellationToken);
}
