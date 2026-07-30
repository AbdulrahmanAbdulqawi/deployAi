namespace DeployAI.Core.Deployments;

/// <summary>What shape of thing the app said it was missing.</summary>
public enum MissingConfigurationKind
{
    /// <summary>A named environment variable, usable as a key exactly as reported.</summary>
    EnvironmentVariable,

    /// <summary>
    /// A configuration section rather than a single key — .NET's "Jwt configuration missing" names
    /// the section, not which of <c>Jwt__Key</c>/<c>Jwt__Issuer</c>/… is absent. The section is all
    /// the app told us, so it is all this reports; completing the key is the user's call.
    /// </summary>
    ConfigurationSection
}

/// <summary>Something an application said it needed at startup and did not have.</summary>
/// <param name="Name">The variable or section name exactly as the application reported it.</param>
/// <param name="Kind">Whether <paramref name="Name"/> is a whole key or only a section prefix.</param>
/// <param name="Evidence">The log line it was read from, so the UI can show its working.</param>
public sealed record MissingConfiguration(
    string Name,
    MissingConfigurationKind Kind,
    string Evidence);

/// <summary>
/// Reads an application's own startup output for configuration it says is missing.
/// </summary>
/// <remarks>
/// This exists because repository scanning is not sufficient on its own. Detection reads a fixed
/// set of files at a repository's root, so an app whose configuration lives elsewhere — a monorepo
/// with its API several directories down — produces no variables to ask for, and the app is
/// deployed with nothing set. It then says precisely what it needed, in its own logs, seconds
/// before dying. That is the most reliable statement of an app's requirements available, and until
/// now nothing read it.
/// </remarks>
public interface IMissingConfigurationDetector
{
    /// <summary>
    /// Extracts what the container reported as missing. Empty when the logs say nothing about
    /// configuration — a crash has many causes, and only this one is actionable here.
    /// </summary>
    IReadOnlyList<MissingConfiguration> Detect(IReadOnlyList<string> logLines);
}
