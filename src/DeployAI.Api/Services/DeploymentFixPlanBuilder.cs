using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

internal static class DeploymentFixPlanBuilder
{
    internal static IReadOnlyList<DeploymentPlanPart> BuildSyntheticSplitOriginParts(
        string providerName,
        string? framework,
        DeployTargetConfig targetConfig)
    {
        if (!IsAngularFramework(framework))
        {
            return [];
        }

        var normalizedProvider = providerName.Trim().ToLowerInvariant();
        if (normalizedProvider is not "vercel")
        {
            return [];
        }

        var website = new DeploymentPlanPart(
            "website",
            "vercel",
            targetConfig.RootDirectory,
            targetConfig.ServiceDirectory,
            targetConfig.BuildCommand,
            targetConfig.InstallCommand,
            targetConfig.StartCommand,
            targetConfig.OutputDirectory,
            framework,
            targetConfig.DockerfilePath,
            null);

        var server = new DeploymentPlanPart(
            "server",
            "railway",
            null,
            targetConfig.ServiceDirectory,
            null,
            null,
            null,
            null,
            "dotnet",
            targetConfig.DockerfilePath,
            null);

        return [website, server];
    }

    private static bool IsAngularFramework(string? framework) =>
        !string.IsNullOrWhiteSpace(framework) &&
        framework.Contains("angular", StringComparison.OrdinalIgnoreCase);
}
