namespace DeployAI.Data.Entities;

public class NotificationPreference
{
    public Guid UserId { get; set; }
    public bool EmailOnSuccess { get; set; } = true;
    public bool EmailOnFailure { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
