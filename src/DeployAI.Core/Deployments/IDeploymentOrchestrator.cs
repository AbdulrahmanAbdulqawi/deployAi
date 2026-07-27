namespace DeployAI.Core.Deployments;

/// <summary>Starts deployments - creates the deployment record and queues background jobs to run each target.</summary>
public interface IDeploymentOrchestrator
{
    /// <summary>Triggers a deployment of every deploy target on a project.</summary>
    Task<TriggerDeploymentResult> TriggerAsync(
        Guid projectId,
        Guid userId,
        string branch,
        CancellationToken cancellationToken,
        DeploymentTriggeredBy triggeredBy = DeploymentTriggeredBy.User);

    /// <summary>Triggers a deployment of a single deploy target only.</summary>
    Task<TriggerDeploymentResult> TriggerTargetAsync(
        Guid projectId,
        Guid userId,
        Guid deployTargetId,
        string branch,
        CancellationToken cancellationToken);
}
