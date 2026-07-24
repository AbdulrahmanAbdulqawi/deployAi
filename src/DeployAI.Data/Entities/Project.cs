namespace DeployAI.Data.Entities;

public class Project
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LogoKey { get; set; }
    public string GitHubRepoFullName { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? EnvironmentSyncJson { get; set; }
    public string? DeploymentSetupJson { get; set; }
    public bool AutoDeployEnabled { get; set; }
    public long? GitHubWebhookId { get; set; }
    public string? HealthJson { get; set; }

    /// <summary>
    /// The project's environment variables as an encrypted JSON blob
    /// ({ name: { value, isSecret, isBuildTime } }), same discipline as
    /// ProviderCredential.TokenEncrypted. Values are also pushed to the deploy target;
    /// this copy exists so a redeploy or re-sync doesn't require retyping secrets.
    /// </summary>
    public byte[]? EnvironmentVariablesEncrypted { get; set; }

    public User User { get; set; } = null!;
    public ICollection<DeployTarget> DeployTargets { get; set; } = [];
    public ICollection<Deployment> Deployments { get; set; } = [];
}
