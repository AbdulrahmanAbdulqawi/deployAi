namespace DeployAI.Data.Entities;

public class DeploymentTarget
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid DeployTargetId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderDeploymentId { get; set; }
    public string Status { get; set; } = DeploymentStatuses.Pending;
    public string? DeployUrl { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureAnalysisJson { get; set; }

    public Deployment Deployment { get; set; } = null!;
    public DeployTarget DeployTarget { get; set; } = null!;
    public ICollection<DeploymentLog> Logs { get; set; } = [];
}
