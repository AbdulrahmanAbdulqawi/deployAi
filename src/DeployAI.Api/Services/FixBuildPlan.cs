namespace DeployAI.Api.Services;

public sealed record FixBuildPlan(
    string WorkingDirectory,
    string? InstallCommand,
    string BuildCommand);

public sealed record FixBuildResult(
    bool Succeeded,
    int ExitCode,
    string Output);
