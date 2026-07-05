namespace DeployAI.Data.Entities;

public class Project
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GitHubRepoFullName { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;
    public ICollection<DeployTarget> DeployTargets { get; set; } = [];
    public ICollection<Deployment> Deployments { get; set; } = [];
}
