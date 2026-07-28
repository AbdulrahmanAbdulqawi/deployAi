namespace DeployAI.Core.Deployments;

/// <summary>The immediate (pre-async-completion) result of triggering a deployment - id, overall status, and each target's initial status.</summary>
public sealed record TriggerDeploymentResult(
    Guid DeploymentId,
    string Status,
    IReadOnlyList<TriggerDeploymentTargetResult> Targets);

/// <summary>A single deploy target's initial status right after being triggered.</summary>
public sealed record TriggerDeploymentTargetResult(string ProviderName, string Status);
