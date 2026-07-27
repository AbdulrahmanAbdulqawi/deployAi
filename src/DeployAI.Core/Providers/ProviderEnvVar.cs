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
    bool ValueHidden);

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
    string? CoolifyServerUuid = null,
    string? CoolifyEnvironmentName = null,
    string? CoolifyGithubAppUuid = null,
    string? CoolifyBuildPack = null);

/// <summary>A key/value to create or update as an environment variable on a provider project.</summary>
public sealed record UpsertProviderEnvVarRequest(
    string Key,
    string Value,
    string Type,
    IReadOnlyList<string> Targets);
