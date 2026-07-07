namespace DeployAI.Core.Deployments;

public interface IDeploymentReadinessService
{
    Task<DeploymentReadinessResult> ScanRepositoryAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        IReadOnlyList<DeploymentPlanPart> parts,
        CancellationToken cancellationToken);

    Task<DeploymentReadinessResult> ScanProjectAsync(
        Guid projectId,
        string? gitRef,
        CancellationToken cancellationToken);
}
