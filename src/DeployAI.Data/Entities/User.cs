namespace DeployAI.Data.Entities;

public class User
{
    public Guid Id { get; set; }
    public long GitHubId { get; set; }
    public string GitHubLogin { get; set; } = string.Empty;
    public string? Email { get; set; }
    public byte[] GitHubTokenEncrypted { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<ProviderCredential> ProviderCredentials { get; set; } = [];
    public ICollection<Project> Projects { get; set; } = [];
}
