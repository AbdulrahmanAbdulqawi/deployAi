namespace DeployAI.Core.Providers;

/// <summary>
/// An environment variable on a provider project. <see cref="Value"/> is null when
/// <see cref="ValueHidden"/> is true (the provider doesn't return secret values on read).
/// </summary>
public sealed record ProviderEnvVar(
    string Id,
    string Key,
    string? Value,
    string Type,
    IReadOnlyList<string> Targets,
    bool ValueHidden,
    /// <summary>
    /// Whether the provider makes this value available to the image build, not only at runtime.
    /// Read back so a caller can tell that a key it needs at build time exists as runtime-only —
    /// Coolify cannot promote such a record through an upsert (its update path guards
    /// <c>is_buildtime</c> with <c>has()</c>), so the caller has to delete and recreate it.
    /// </summary>
    bool IsBuildTime = false);

/// <summary>Everything needed to create a new project/application on a provider for a GitHub repo.</summary>
public sealed record CreateProviderProjectRequest(
    string Name,
    string GitHubRepoFullName,
    string? Framework,
    string? RootDirectory = null,
    string? OutputDirectory = null,
    string? BuildCommand = null,
    string? InstallCommand = null,
    string? DockerfilePath = null,
    string? ServiceDirectory = null,
    string? StartCommand = null,
    string? GitBranch = null,
    bool IsPrivateRepository = false,
    string? CoolifyProjectUuid = null,
    /// <summary>
    /// Name of a Coolify project to create the application in, creating the project when no
    /// project already carries that name. An explicit name is a choice, which is what lets the
    /// provider create a project on an instance that already has several — without one it
    /// refuses to guess. Ignored when <see cref="CoolifyProjectUuid"/> is set.
    /// </summary>
    string? CoolifyProjectName = null,
    string? CoolifyServerUuid = null,
    string? CoolifyEnvironmentName = null,
    string? CoolifyGithubAppUuid = null,
    string? CoolifyBuildPack = null,
    /// <summary>
    /// Path to the compose file, relative to the repo root. First-class rather than assumed,
    /// because the file we generate is docker-compose.coolify.yml and Coolify otherwise looks
    /// for docker-compose.yml — which in most repos is the local dev stack.
    /// </summary>
    string? ComposeFileLocation = null,
    /// <summary>
    /// Domain to attach after creation. Without one, Coolify only autogenerates a sslip.io
    /// hostname; with a compose app it may route nothing at all.
    /// </summary>
    string? CustomDomain = null,
    /// <summary>
    /// Compose service the domain attaches to (e.g. <c>web</c>). The other services stay
    /// internal and are reached over the compose network.
    /// </summary>
    string? DomainServiceName = null,
    /// <summary>
    /// The container port Coolify should route to (its <c>ports_exposes</c>). When we generate the
    /// Dockerfile we know the port it EXPOSEs (e.g. 8080 for .NET), and Coolify does not infer it —
    /// left unset, it defaults the proxy to 3000 and a .NET app 502s. Overrides framework guessing.
    /// </summary>
    string? ExposedPort = null,
    /// <summary>
    /// Whether the provider should redeploy on its own when the branch is pushed. Off by default,
    /// and it must stay off unless the user asked for it: DeployAI writes generated files
    /// (Dockerfiles, compose files) into the repository as part of a publish, and with provider
    /// auto-deploy on, each of those commits fires a webhook build that races the deploy DeployAI
    /// is already orchestrating for the same commit.
    /// </summary>
    bool AutoDeployEnabled = false);

/// <summary>A key/value to create or update as an environment variable on a provider project.</summary>
public sealed record UpsertProviderEnvVarRequest(
    string Key,
    string Value,
    string Type,
    IReadOnlyList<string> Targets);

public static class ProviderEnvVarTypes
{
    public const string Plain = "plain";

    /// <summary>
    /// A value that must not be echoed back or logged. Coolify keeps these write-only.
    /// </summary>
    public const string Secret = "secret";

    /// <summary>
    /// Needed at image-build time, not just at runtime — a frontend bundle bakes these in.
    /// </summary>
    public const string BuildTime = "build";

    public static bool IsSecret(string? type) =>
        string.Equals(type, Secret, StringComparison.OrdinalIgnoreCase);

    public static bool IsBuildTime(string? type) =>
        string.Equals(type, BuildTime, StringComparison.OrdinalIgnoreCase);
}
