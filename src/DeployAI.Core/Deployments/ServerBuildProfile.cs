namespace DeployAI.Core.Deployments;

/// <summary>A detected backend build configuration (.NET/Node/Dockerfile), used to pre-fill a server deploy target.</summary>
public sealed record ServerBuildProfile(

    string RootDirectory,

    string? BuildCommand,

    string? InstallCommand,

    string? StartCommand,

    string? Framework,

    string? DockerfilePath = null,

    string? ServiceDirectory = null,

    bool DockerUsesRepositoryRoot = false);

