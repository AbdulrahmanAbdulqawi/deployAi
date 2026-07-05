namespace DeployAI.Core.Providers;

public sealed record ProviderEnvVar(
    string Id,
    string Key,
    string? Value,
    string Type,
    IReadOnlyList<string> Targets,
    bool ValueHidden);

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
    string? StartCommand = null);

public sealed record UpsertProviderEnvVarRequest(
    string Key,
    string Value,
    string Type,
    IReadOnlyList<string> Targets);
