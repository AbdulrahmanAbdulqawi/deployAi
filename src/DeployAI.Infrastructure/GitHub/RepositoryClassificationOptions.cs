namespace DeployAI.Infrastructure.GitHub;

/// <summary>
/// Coolify is the default deployment target. Vercel/Railway are the opt-in legacy path,
/// which is why the flag is phrased as an exception rather than a preference.
/// </summary>
public sealed record RepositoryClassificationOptions(bool PreferVercelRailway = false);
