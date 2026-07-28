using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;

namespace DeployAI.Api.Services;

/// <summary>Detects whether a website+server pair (or a full deployment plan) needs split-origin setup files/wiring, and resolves which readiness files apply.</summary>
internal static class SplitOriginDetection
{
    /// <summary>Whether a website+server pair both needs and is eligible for split-origin wiring (a supported provider pair, and framework combination that resolves to split-origin mode).</summary>
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

        return CrossProviderUrlWiring.ResolveWiringMode(
                   websiteFramework,
                   serverFramework,
                   IsSingleOriginComposeStack(websiteFramework, serverFramework, websiteProvider, serverProvider)) ==
               CrossProviderWiringMode.SplitOrigin;
    }

    /// <summary>
    /// One Coolify server hosting both halves: they belong in one compose file behind one
    /// origin, so none of the split-origin scaffolding, CORS wiring, or readiness checks apply.
    /// </summary>
    internal static bool IsSingleOriginComposeStack(
        string? websiteFramework,
        string? serverFramework,
        string? websiteProvider,
        string? serverProvider) =>
        SingleOriginComposeShape.Supports(websiteFramework, serverFramework, websiteProvider, serverProvider);

    internal static bool PlanUsesSingleOriginCompose(IReadOnlyList<DeploymentPlanPart> parts)
    {
        var website = FindWebsitePart(parts);
        var server = FindServerPart(parts);
        if (website is null || server is null)
        {
            return false;
        }

        return IsSingleOriginComposeStack(
            website.Framework, server.Framework, website.ProviderName, server.ProviderName);
    }

    internal static bool IsCoolifyFullStack(string? websiteProvider, string? serverProvider) =>
        string.Equals(websiteProvider, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(serverProvider, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedSplitOriginProviderPair(string? websiteProvider, string? serverProvider) =>
        (string.Equals(websiteProvider, "vercel", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(serverProvider, "railway", StringComparison.OrdinalIgnoreCase)) ||
        IsCoolifyFullStack(websiteProvider, serverProvider);

    /// <summary>Whether a plan part is a Vercel website using relative API paths - the shape that needs split-origin wiring when paired with a .NET server.</summary>
    internal static bool IsSplitOriginPlanPart(DeploymentPlanPart part) =>
        string.Equals(part.Role, "website", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(part.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase) &&
        CrossProviderUrlWiring.UsesRelativeApiPaths(part.Framework);

    /// <summary>Returns the first website-role part, regardless of provider - ambiguous when a plan has more than one website part; prefer <see cref="ResolveProviderPairs"/> for that case.</summary>
    internal static DeploymentPlanPart? FindWebsitePart(IReadOnlyList<DeploymentPlanPart> parts) =>
        parts.FirstOrDefault(p => string.Equals(p.Role, "website", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the first server-role part, regardless of provider - ambiguous when a plan has more than one server part; prefer <see cref="ResolveProviderPairs"/> for that case.</summary>
    internal static DeploymentPlanPart? FindServerPart(IReadOnlyList<DeploymentPlanPart> parts) =>
        parts.FirstOrDefault(p => string.Equals(p.Role, "server", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns every valid (website, server) pair found among the given plan parts, grouped by
    /// matching provider (Vercel+Railway, Coolify+Coolify) - unlike FindWebsitePart/FindServerPart,
    /// which return only the first website-role/server-role part regardless of provider and would
    /// silently drop readiness checks for a second pair (e.g. a project with both a Vercel+Railway
    /// pair and a Coolify+Coolify pair).
    /// </summary>
    internal static IReadOnlyList<(DeploymentPlanPart Website, DeploymentPlanPart Server)> ResolveProviderPairs(
        IReadOnlyList<DeploymentPlanPart> parts)
    {
        var websites = parts.Where(p => string.Equals(p.Role, "website", StringComparison.OrdinalIgnoreCase)).ToList();
        var servers = parts.Where(p => string.Equals(p.Role, "server", StringComparison.OrdinalIgnoreCase)).ToList();

        var pairs = new List<(DeploymentPlanPart, DeploymentPlanPart)>();
        foreach (var website in websites)
        {
            var server = servers.FirstOrDefault(
                s => IsSupportedSplitOriginProviderPair(website.ProviderName, s.ProviderName));
            if (server is not null)
            {
                pairs.Add((website, server));
            }
        }

        return pairs;
    }

    /// <summary>Whether a plan's first website+server pair needs split-origin wiring. Note this only checks the first pair - a mixed multi-pair plan may have some pairs that do and some that don't.</summary>
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

    /// <summary>
    /// The files this plan's shape requires. Dispatches by shape — callers ask for "what does
    /// this plan need" and must not assume split-origin.
    /// </summary>
    internal static IReadOnlyList<string> BuildReadinessFilePaths(DeploymentPlanPart websitePart, DeploymentPlanPart serverPart) =>
        IsSingleOriginComposeStack(
            websitePart.Framework, serverPart.Framework, websitePart.ProviderName, serverPart.ProviderName)
            ? SingleOriginComposeReadinessEvaluator.BuildReadinessFilePaths(websitePart, serverPart)
            : BuildSplitOriginReadinessFilePaths(websitePart, serverPart);

    internal static IReadOnlyList<string> BuildSplitOriginReadinessFilePaths(
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart)
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

    /// <summary>Evaluates which split-origin setup files are missing from a repo's fetched file contents; returns empty if the pair doesn't use split-origin at all.</summary>
    internal static IReadOnlyList<MissingDeploymentFile> EvaluateRepositoryFiles(
        bool usesSplitOrigin,
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart,
        IReadOnlyDictionary<string, string?> fileContentsByPath)
    {
        if (IsSingleOriginComposeStack(
                websitePart.Framework, serverPart.Framework, websitePart.ProviderName, serverPart.ProviderName))
        {
            return SingleOriginComposeReadinessEvaluator.Evaluate(websitePart, serverPart, fileContentsByPath);
        }

        return usesSplitOrigin
            ? SplitOriginReadinessEvaluator.Evaluate(websitePart, serverPart, fileContentsByPath)
            : [];
    }

    private static string NormalizeRoot(string? root) =>
        root?.Trim().Trim('/') ?? string.Empty;
}
