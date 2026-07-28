namespace DeployAI.Core.Providers;

/// <summary>A provider deployment's coarse-grained lifecycle state, as polled from <see cref="IDeploymentProvider.GetStatusAsync"/>.</summary>
public enum DeploymentStatusKind
{
    Pending,
    InProgress,
    Success,
    Failed
}

/// <summary>A deployment's current status, its URL once known, and an error message if it failed.</summary>
public sealed record DeploymentStatus(
    DeploymentStatusKind Status,
    string? DeployUrl,
    string? ErrorMessage);
