using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;

namespace DeployAI.Api.Services;

internal static class SplitOriginDetection
{
    internal static bool IsSplitOriginStack(
        string? websiteFramework,
        string? serverFramework,
        string? websiteProvider,
        string? serverProvider)
    {
        if (!IsSupportedSplitOriginProviderPair(websiteProvider, serverProvider))
        {
            return false;
        }

        return CrossProviderUrlWiring.ResolveWiringMode(websiteFramework, serverFramework) ==
               CrossProviderWiringMode.SplitOrigin;
    }

    internal static bool IsCoolifyFullStack(string? websiteProvider, string? serverProvider) =>
        string.Equals(websiteProvider, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(serverProvider, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedSplitOriginProviderPair(string? websiteProvider, string? serverProvider) =>
        (string.Equals(websiteProvider, "vercel", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(serverProvider, "railway", StringComparison.OrdinalIgnoreCase)) ||
        IsCoolifyFullStack(websiteProvider, serverProvider);

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
        var coolifyStack = IsCoolifyFullStack(websitePart.ProviderName, serverPart.ProviderName);

        var paths = new List<string>
        {
            $"{clientPrefix}scripts/write-api-env.mjs",
            $"{clientPrefix}src/app/core/interceptors/api-base.interceptor.ts",
            $"{serverRoot}/Controllers/HealthController.cs"
        };

        if (!coolifyStack)
        {
            paths.Insert(0, "railway.toml");
            paths.Insert(1, $"{clientPrefix}vercel.json");
        }

        return paths;
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
