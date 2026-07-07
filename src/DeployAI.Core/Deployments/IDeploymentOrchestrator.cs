namespace DeployAI.Core.Deployments;

public interface IDeploymentOrchestrator
{
    Task<TriggerDeploymentResult> TriggerAsync(Guid projectId, Guid userId, string branch, CancellationToken cancellationToken);

    Task<TriggerDeploymentResult> TriggerTargetAsync(
        Guid projectId,
        Guid userId,
        Guid deployTargetId,
        string branch,
        CancellationToken cancellationToken);
}
