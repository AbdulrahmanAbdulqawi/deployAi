namespace DeployAI.Data.Entities;

public class User
{
    public Guid Id { get; set; }
    public long GitHubId { get; set; }
    public string GitHubLogin { get; set; } = string.Empty;
    public string? Email { get; set; }
    public byte[] GitHubTokenEncrypted { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Credentials the user saved once so every app that needs them is filled in rather than asked.
    /// </summary>
    /// <remarks>
    /// For values DeployAI can never obtain on its own. A GitHub App's private key is the case this
    /// exists for: GitHub shows it once at creation and cannot reissue it, so it cannot be generated
    /// and cannot be read back from anywhere — and without somewhere to keep it, every app that
    /// needs one asks the user to paste it again, which the standing rule says is a gap.
    /// </remarks>
    public byte[]? SavedSecretsEncrypted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<ProviderCredential> ProviderCredentials { get; set; } = [];
    public ICollection<Project> Projects { get; set; } = [];
}
