namespace DeployAI.Data.Entities;

public class AgentMemoryFile
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }

    public Project Project { get; set; } = null!;
}
