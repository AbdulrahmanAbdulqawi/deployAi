namespace DeployAI.Core.Deployments.Graph;

/// <summary>
/// An environment variable a service was observed to need, merged from every source that
/// mentioned it. Language-agnostic: the same record comes out of a compose ${VAR}, a
/// Dockerfile ARG, a .env.example line, an appsettings key, a README block, or a CI secret.
/// </summary>
public sealed record DetectedEnvVar(
    string Name,
    /// <summary>Never echo or prefill visibly; store write-only on the provider.</summary>
    bool IsSecret,
    bool HasDefault = false,
    string? DefaultValue = null,
    EnvVarCategory Category = EnvVarCategory.Generic,
    /// <summary>Which scanners saw it ("compose", "dotenv", "dockerfile", …) — kept for the UI to explain itself.</summary>
    IReadOnlyList<string>? Sources = null)
{
    public IReadOnlyList<string> SeenIn => Sources ?? [];
}

/// <summary>
/// Drives smart-fill: a Domain var is prefilled from the chosen domain, Storage vars from a
/// connected storage account, AdminEmail from the signed-in user. Generic gets no prefill.
/// </summary>
public enum EnvVarCategory
{
    Generic,
    Domain,
    Storage,
    Database,
    AdminEmail
}
