namespace DeployAI.Core.Deployments;

public sealed record TriggerDeploymentResult(
    Guid DeploymentId,
    string Status,
    IReadOnlyList<TriggerDeploymentTargetResult> Targets);

public sealed record TriggerDeploymentTargetResult(string ProviderName, string Status);
