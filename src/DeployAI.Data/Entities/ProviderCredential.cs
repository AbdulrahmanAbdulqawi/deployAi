namespace DeployAI.Data.Entities;

public class ProviderCredential
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public byte[] TokenEncrypted { get; set; } = Array.Empty<byte>();
    public string Label { get; set; } = "Default";
    public bool IsValid { get; set; }
    public DateTimeOffset? LastValidatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
    public ICollection<DeployTarget> DeployTargets { get; set; } = [];
}
