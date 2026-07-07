namespace DeployAI.Data.Entities;

public static class DeploymentStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Partial = "partial";
    public const string Cancelled = "cancelled";
}

public class Deployment
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Branch { get; set; } = string.Empty;
    public string TriggeredBy { get; set; } = "user";
    public string Status { get; set; } = DeploymentStatuses.Pending;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? GitCommitSha { get; set; }
    public string? GitCommitMessage { get; set; }

    public Project Project { get; set; } = null!;
    public ICollection<DeploymentTarget> Targets { get; set; } = [];
}
