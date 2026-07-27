using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;

namespace DeployAI.Api.Services;

/// <summary>
/// Reconstructs a synthetic split-origin (website, server) part pair from just one known target's
/// config, for cases where the fix flow only has one side's config on hand but needs the pair shape
/// split-origin readiness/template resolution expects.
/// </summary>
internal static class DeploymentFixPlanBuilder
{
    private const string DefaultClientRoot = "client";
    private const string DefaultServerRoot = "src/Api";

    /// <summary>Builds the missing counterpart part (website or server) from a known target's config, inferring reasonable defaults for the other side. Returns empty if the framework/provider combination isn't a supported split-origin shape.</summary>
    internal static IReadOnlyList<DeploymentPlanPart> BuildSyntheticSplitOriginParts(
        string providerName,
        string? framework,
        DeployTargetConfig targetConfig)
    {
        var normalizedProvider = providerName.Trim().ToLowerInvariant();
        if (normalizedProvider == ProviderNameValues.Coolify)
        {
            if (!IsCoolifySplitOriginFixTarget(framework, targetConfig.Role))
            {
                return [];
            }

            return BuildCoolifyFullStackParts(targetConfig);
        }

        if (normalizedProvider != ProviderNameValues.Vercel || !IsAngularFramework(framework))
        {
            return [];
        }

        return BuildVercelRailwayParts(targetConfig);
    }

    private static IReadOnlyList<DeploymentPlanPart> BuildVercelRailwayParts(DeployTargetConfig targetConfig)
    {
        var website = new DeploymentPlanPart(
            "website",
            ProviderNameValues.Vercel,
            targetConfig.RootDirectory,
            targetConfig.ServiceDirectory,
            targetConfig.BuildCommand,
            targetConfig.InstallCommand,
            targetConfig.StartCommand,
            targetConfig.OutputDirectory,
            targetConfig.Framework ?? "angular",
            targetConfig.DockerfilePath,
            null);

        var serverDirectory = targetConfig.ServiceDirectory ?? DefaultServerRoot;
        var server = new DeploymentPlanPart(
            "server",
            ProviderNameValues.Railway,
            null,
            serverDirectory,
            null,
            null,
            null,
            null,
            "dotnet",
            targetConfig.DockerfilePath,
            null);

        return [website, server];
    }

    private static IReadOnlyList<DeploymentPlanPart> BuildCoolifyFullStackParts(DeployTargetConfig targetConfig)
    {
        var isServerFix = string.Equals(targetConfig.Role, "server", StringComparison.OrdinalIgnoreCase);
        var serverDirectory = NormalizeRoot(targetConfig.ServiceDirectory ?? targetConfig.RootDirectory) ?? DefaultServerRoot;

        var website = isServerFix
            ? new DeploymentPlanPart(
                "website",
                ProviderNameValues.Coolify,
                DefaultClientRoot,
                null,
                null,
                null,
                null,
                null,
                "angular",
                null,
                null)
            : new DeploymentPlanPart(
                "website",
                ProviderNameValues.Coolify,
                targetConfig.RootDirectory ?? DefaultClientRoot,
                targetConfig.ServiceDirectory,
                targetConfig.BuildCommand,
                targetConfig.InstallCommand,
                targetConfig.StartCommand,
                targetConfig.OutputDirectory,
                targetConfig.Framework ?? "angular",
                targetConfig.DockerfilePath,
                null);

        var server = new DeploymentPlanPart(
            "server",
            ProviderNameValues.Coolify,
            null,
            serverDirectory,
            isServerFix ? targetConfig.BuildCommand : null,
            isServerFix ? targetConfig.InstallCommand : null,
            isServerFix ? targetConfig.StartCommand : null,
            null,
            targetConfig.Framework is not null && IsDotnetFramework(targetConfig.Framework)
                ? targetConfig.Framework
                : "dotnet",
            targetConfig.DockerfilePath,
            null);

        return [website, server];
    }

    private static bool IsCoolifySplitOriginFixTarget(string? framework, string? role) =>
        IsAngularFramework(framework) ||
        (string.Equals(role, "server", StringComparison.OrdinalIgnoreCase) && IsDotnetFramework(framework));

    private static bool IsAngularFramework(string? framework) =>
        !string.IsNullOrWhiteSpace(framework) &&
        framework.Contains("angular", StringComparison.OrdinalIgnoreCase);

    private static bool IsDotnetFramework(string? framework) =>
        framework?.ToLowerInvariant() is "dotnet" or "aspnet" or "aspnetcore" or "docker";

    private static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        return root.Trim().Trim('/');
    }
}
