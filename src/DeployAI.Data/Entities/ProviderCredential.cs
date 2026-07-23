namespace DeployAI.Data.Entities;

/// <summary>
/// What a credential is for. Deployment credentials feed deploy-target pickers;
/// object-storage credentials must be excluded from those, so they are kept apart here
/// rather than being distinguished by provider-name string matching.
/// </summary>
public enum CredentialKind
{
    Deployment = 0,
    ObjectStorage = 1
}

public class ProviderCredential
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public CredentialKind Kind { get; set; } = CredentialKind.Deployment;
    public byte[] TokenEncrypted { get; set; } = Array.Empty<byte>();
    public string Label { get; set; } = "Default";
    public bool IsValid { get; set; }
    public DateTimeOffset? LastValidatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
    public ICollection<DeployTarget> DeployTargets { get; set; } = [];
}
