namespace DeployAI.Core.Deployments;

/// <summary>
/// What a deployment part is for. Role — not provider name — is what dispatch should key off:
/// two parts can share a provider (both halves on one Coolify server) and still be different
/// things, so matching on the provider collapses them into one.
/// </summary>
public static class DeploymentPartRoles
{
    public const string Website = "website";

    public const string Server = "server";

    public const string Database = "database";

    /// <summary>
    /// An object storage bucket. Provisioned like a database and wired in as env vars — never a
    /// deploy target, so it must not appear in provider pickers or progress bars.
    /// </summary>
    public const string Storage = "storage";

    public static bool Is(string? role, string expected) =>
        string.Equals(role, expected, StringComparison.OrdinalIgnoreCase);
}
