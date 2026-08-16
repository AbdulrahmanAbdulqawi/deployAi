namespace DeployAI.Data.Entities;

/// <summary>
/// What a credential is for. Deployment credentials feed deploy-target pickers;
/// object-storage credentials must be excluded from those, so they are kept apart here
/// rather than being distinguished by provider-name string matching.
/// </summary>
public enum CredentialKind
{
    Deployment = 0,
    ObjectStorage = 1,

    /// <summary>
    /// A DNS account DeployAI writes records into. Kept apart for the same reason as object
    /// storage: it must never appear in a deploy-target picker, and the kind says so without
    /// anyone having to match on the provider's name.
    /// </summary>
    Dns = 2
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

    /// <summary>
    /// When the credential stops working, where the provider tells us. Null means no expiry is
    /// known — which is not the same as "never expires", and must not be shown as if it were.
    /// </summary>
    /// <remarks>
    /// Recorded so a token can be replaced before it lapses rather than after. Without it the
    /// first sign of an expired DNS token is DNS automation silently reverting to asking the user
    /// to add records by hand, with nothing anywhere connecting the two.
    /// </remarks>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
    public ICollection<DeployTarget> DeployTargets { get; set; } = [];
}
