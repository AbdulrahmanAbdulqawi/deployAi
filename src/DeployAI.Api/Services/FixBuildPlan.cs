namespace DeployAI.Api.Services;

/// <summary>The resolved commands a fix-generation build sandbox should run to install and build a target, to validate a Claude-generated fix before submitting it.</summary>
public sealed record FixBuildPlan(
    string WorkingDirectory,
    string? InstallCommand,
    string BuildCommand);

/// <summary>The outcome of running a <see cref="FixBuildPlan"/> in the sandbox.</summary>
public sealed record FixBuildResult(
    bool Succeeded,
    int ExitCode,
    string Output);
