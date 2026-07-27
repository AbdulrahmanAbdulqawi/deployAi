namespace DeployAI.Core.Deployments;

/// <summary>A detected frontend build configuration (from angular.json/package.json), used to pre-fill a website deploy target.</summary>
public sealed record FrontendBuildProfile(
    string RootDirectory,
    string BuildCommand,
    string InstallCommand,
    string OutputDirectory,
    string? Framework);
