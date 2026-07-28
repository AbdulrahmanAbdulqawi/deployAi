namespace DeployAI.Core.Deployments;

/// <summary>The result of switching a project's branch and, if requested, deploying it - the new deployment's id if one was triggered.</summary>
public sealed record UseBranchDeployResult(
    string Branch,
    Guid? DeploymentId,
    string? Message);
