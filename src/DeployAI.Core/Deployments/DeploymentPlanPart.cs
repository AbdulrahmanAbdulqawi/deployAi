namespace DeployAI.Core.Deployments;

public sealed record DeploymentPlanPart(
    string Role,
    string ProviderName,
    string? RootDirectory = null,
    string? ServiceDirectory = null,
    string? BuildCommand = null,
    string? InstallCommand = null,
    string? StartCommand = null,
    string? OutputDirectory = null,
    string? Framework = null,
    string? DockerfilePath = null,
    string? DatabaseEngine = null);
