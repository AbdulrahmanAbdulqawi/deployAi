namespace DeployAI.Core.Deployments;

public sealed record UseBranchDeployResult(
    string Branch,
    Guid? DeploymentId,
    string? Message);
