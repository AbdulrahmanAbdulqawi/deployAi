using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

internal static class SplitOriginDetection
{
    internal static bool IsSplitOriginStack(
        string? websiteFramework,
        string? serverFramework,
        string? websiteProvider,
        string? serverProvider)
    {
        if (!string.Equals(websiteProvider, "vercel", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(serverProvider, "railway", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CrossProviderUrlWiring.ResolveWiringMode(websiteFramework, serverFramework) ==
               CrossProviderWiringMode.SplitOrigin;
    }

    internal static bool IsSplitOriginPlanPart(DeploymentPlanPart part) =>
        string.Equals(part.Role, "website", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(part.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase) &&
        CrossProviderUrlWiring.UsesRelativeApiPaths(part.Framework);

    internal static DeploymentPlanPart? FindWebsitePart(IReadOnlyList<DeploymentPlanPart> parts) =>
        parts.FirstOrDefault(p => string.Equals(p.Role, "website", StringComparison.OrdinalIgnoreCase));

    internal static DeploymentPlanPart? FindServerPart(IReadOnlyList<DeploymentPlanPart> parts) =>
        parts.FirstOrDefault(p => string.Equals(p.Role, "server", StringComparison.OrdinalIgnoreCase));

    internal static bool PlanUsesSplitOrigin(IReadOnlyList<DeploymentPlanPart> parts)
    {
        var website = FindWebsitePart(parts);
        var server = FindServerPart(parts);
        if (website is null || server is null)
        {
            return false;
        }

        return IsSplitOriginStack(website.Framework, server.Framework, website.ProviderName, server.ProviderName);
    }

    internal static IReadOnlyList<string> BuildReadinessFilePaths(DeploymentPlanPart websitePart, DeploymentPlanPart serverPart)
    {
        var clientRoot = NormalizeRoot(websitePart.RootDirectory);
        var serverRoot = NormalizeRoot(serverPart.ServiceDirectory ?? serverPart.RootDirectory);
        var clientPrefix = string.IsNullOrEmpty(clientRoot) ? string.Empty : $"{clientRoot}/";

        return
        [
            "railway.toml",
            $"{clientPrefix}vercel.json",
            $"{clientPrefix}scripts/write-api-env.mjs",
            $"{clientPrefix}src/app/core/interceptors/api-base.interceptor.ts",
            $"{serverRoot}/Controllers/HealthController.cs"
        ];
    }

    internal static IReadOnlyList<MissingDeploymentFile> EvaluateRepositoryFiles(
        bool usesSplitOrigin,
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart,
        IReadOnlyDictionary<string, string?> fileContentsByPath) =>
        usesSplitOrigin
            ? SplitOriginReadinessEvaluator.Evaluate(websitePart, serverPart, fileContentsByPath)
            : [];

    private static string NormalizeRoot(string? root) =>
        root?.Trim().Trim('/') ?? string.Empty;
}
