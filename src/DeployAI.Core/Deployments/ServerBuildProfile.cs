namespace DeployAI.Core.Deployments;



public sealed record ServerBuildProfile(

    string RootDirectory,

    string? BuildCommand,

    string? InstallCommand,

    string? StartCommand,

    string? Framework,

    string? DockerfilePath = null,

    string? ServiceDirectory = null,

    bool DockerUsesRepositoryRoot = false);

