namespace DeployAI.Data.Entities;

public class DeploymentLog
{
    public long Id { get; set; }
    public Guid DeploymentTargetId { get; set; }
    public int Sequence { get; set; }
    public string Line { get; set; } = string.Empty;
    public DateTimeOffset LoggedAt { get; set; }

    public DeploymentTarget DeploymentTarget { get; set; } = null!;
}
