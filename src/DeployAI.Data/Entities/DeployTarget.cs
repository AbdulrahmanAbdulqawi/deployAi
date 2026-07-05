namespace DeployAI.Data.Entities;

public class DeployTarget
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public Guid CredentialId { get; set; }
    public string ProviderProjectId { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }

    public Project Project { get; set; } = null!;
    public ProviderCredential Credential { get; set; } = null!;
    public ICollection<DeploymentTarget> DeploymentTargets { get; set; } = [];
}
