namespace DeployAI.Core.Deployments;

public sealed record FrontendBuildProfile(
    string RootDirectory,
    string BuildCommand,
    string InstallCommand,
    string OutputDirectory,
    string? Framework);
