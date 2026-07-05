namespace DeployAI.Core.Providers;

public enum DeploymentStatusKind
{
    Pending,
    InProgress,
    Success,
    Failed
}

public sealed record DeploymentStatus(
    DeploymentStatusKind Status,
    string? DeployUrl,
    string? ErrorMessage);
