namespace DeployAI.Core.Deployments;

/// <summary>
/// One piece of a <see cref="DeploymentPlan"/> - a website, server, or database - with its
/// suggested provider and build config. The same shape is reused for both classified plans and the
/// deploy target configs actually persisted (see <see cref="DeployTargetConfig.FromProfile"/>).
/// </summary>
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
