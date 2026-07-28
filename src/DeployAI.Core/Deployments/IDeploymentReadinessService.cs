namespace DeployAI.Core.Deployments;

/// <summary>Scans a repo or project for split-origin deployment readiness (missing wiring files, warnings).</summary>
public interface IDeploymentReadinessService
{
    /// <summary>Scans a repo at a given ref against a caller-supplied plan, without requiring an existing project.</summary>
    Task<DeploymentReadinessResult> ScanRepositoryAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        IReadOnlyList<DeploymentPlanPart> parts,
        CancellationToken cancellationToken);

    /// <summary>Scans an existing project's linked repo/targets for readiness.</summary>
    Task<DeploymentReadinessResult> ScanProjectAsync(
        Guid projectId,
        string? gitRef,
        CancellationToken cancellationToken);
}
