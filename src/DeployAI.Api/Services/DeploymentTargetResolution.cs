using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data.Entities;

namespace DeployAI.Api.Services;

/// <summary>Resolves website/server targets from a deployment's or project's target list, both by first-match and (preferred for multi-pair projects) by full pair grouping.</summary>
internal static class DeploymentTargetResolution
{
    /// <summary>Returns the first website-role target, or the first Vercel/Coolify target if no role is set. Ambiguous on multi-pair projects - prefer <see cref="ResolveProviderPairs(System.Collections.Generic.IEnumerable{DeploymentTarget})"/> there.</summary>
    internal static DeploymentTarget? FindWebsiteTarget(IEnumerable<DeploymentTarget> targets)
    {
        var list = targets.ToList();
        var byRole = list.FirstOrDefault(IsWebsiteRole);
        if (byRole is not null)
        {
            return byRole;
        }

        return list.FirstOrDefault(target =>
            string.Equals(target.ProviderName, ProviderNameValues.Vercel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the first server-role target, or the first Railway/Coolify target if no role is set. Ambiguous on multi-pair projects - prefer <see cref="ResolveProviderPairs(System.Collections.Generic.IEnumerable{DeploymentTarget})"/> there.</summary>
    internal static DeploymentTarget? FindServerTarget(IEnumerable<DeploymentTarget> targets)
    {
        var list = targets.ToList();
        var byRole = list.FirstOrDefault(IsServerRole);
        if (byRole is not null)
        {
            return byRole;
        }

        return list.FirstOrDefault(target =>
            string.Equals(target.ProviderName, ProviderNameValues.Railway, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the (first-match) resolved website and server targets are both Coolify.</summary>
    internal static bool IsCoolifyFullStack(IEnumerable<DeploymentTarget> targets)
    {
        var website = FindWebsiteTarget(targets);
        var server = FindServerTarget(targets);
        return website is not null &&
               server is not null &&
               string.Equals(website.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(server.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns every valid (website, server) pair found among the given targets, grouped by
    /// matching provider (Vercel+Railway, Coolify+Coolify) - unlike FindWebsiteTarget/FindServerTarget,
    /// which return only the first match across all providers and silently drop any additional pair
    /// (e.g. a project with both a Vercel+Railway pair and a Coolify+Coolify pair).
    /// </summary>
    internal static IReadOnlyList<(DeploymentTarget Website, DeploymentTarget Server)> ResolveProviderPairs(
        IEnumerable<DeploymentTarget> targets)
    {
        var list = targets.ToList();
        var websiteCandidates = ResolveRoleCandidates(list, IsWebsiteRole, IsWebsiteCapableProvider);
        var serverCandidates = ResolveRoleCandidates(list, IsServerRole, IsServerCapableProvider);

        var pairs = new List<(DeploymentTarget, DeploymentTarget)>();
        foreach (var website in websiteCandidates)
        {
            var server = serverCandidates.FirstOrDefault(
                s => IsSupportedProviderPair(website.ProviderName, s.ProviderName));
            if (server is not null)
            {
                pairs.Add((website, server));
            }
        }

        return pairs;
    }

    private static List<DeploymentTarget> ResolveRoleCandidates(
        List<DeploymentTarget> list,
        Func<DeploymentTarget, bool> isRole,
        Func<string, bool> isCapableProvider)
    {
        var byRole = list.Where(isRole).ToList();
        return byRole.Count > 0
            ? byRole
            : list.Where(target => isCapableProvider(target.ProviderName)).ToList();
    }

    private static bool IsWebsiteCapableProvider(string providerName) =>
        string.Equals(providerName, ProviderNameValues.Vercel, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);

    private static bool IsServerCapableProvider(string providerName) =>
        string.Equals(providerName, ProviderNameValues.Railway, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedProviderPair(string websiteProvider, string serverProvider) =>
        (string.Equals(websiteProvider, ProviderNameValues.Vercel, StringComparison.OrdinalIgnoreCase) &&
         string.Equals(serverProvider, ProviderNameValues.Railway, StringComparison.OrdinalIgnoreCase)) ||
        (string.Equals(websiteProvider, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
         string.Equals(serverProvider, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase));

    private static bool IsWebsiteRole(DeploymentTarget target) =>
        string.Equals(ResolveRole(target), "website", StringComparison.OrdinalIgnoreCase);

    private static bool IsServerRole(DeploymentTarget target) =>
        string.Equals(ResolveRole(target), "server", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveRole(DeploymentTarget target) =>
        DeployTargetConfig.Parse(target.DeployTarget.ConfigJson).Role;

    /// <summary>
    /// Project-scoped overload of <see cref="ResolveProviderPairs(IEnumerable{DeploymentTarget})"/>
    /// operating directly on a project's persisted <see cref="DeployTarget"/> configs (as used by
    /// FrontendEnvironmentWiringService), rather than a specific deployment run's targets.
    /// </summary>
    internal static IReadOnlyList<(DeployTarget Website, DeployTarget Server)> ResolveProviderPairs(
        IEnumerable<DeployTarget> targets)
    {
        var list = targets.Where(t => !DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget).ToList();
        var websiteCandidates = ResolveRoleCandidates(list, IsWebsiteRole, IsWebsiteCapableProvider);
        var serverCandidates = ResolveRoleCandidates(list, IsServerRole, IsServerCapableProvider);

        var pairs = new List<(DeployTarget, DeployTarget)>();
        foreach (var website in websiteCandidates)
        {
            var server = serverCandidates.FirstOrDefault(
                s => IsSupportedProviderPair(website.ProviderName, s.ProviderName));
            if (server is not null)
            {
                pairs.Add((website, server));
            }
        }

        return pairs;
    }

    private static List<DeployTarget> ResolveRoleCandidates(
        List<DeployTarget> list,
        Func<DeployTarget, bool> isRole,
        Func<string, bool> isCapableProvider)
    {
        var byRole = list.Where(isRole).ToList();
        return byRole.Count > 0
            ? byRole
            : list.Where(target => isCapableProvider(target.ProviderName)).ToList();
    }

    private static bool IsWebsiteRole(DeployTarget target) =>
        string.Equals(DeployTargetConfig.Parse(target.ConfigJson).Role, "website", StringComparison.OrdinalIgnoreCase);

    private static bool IsServerRole(DeployTarget target) =>
        string.Equals(DeployTargetConfig.Parse(target.ConfigJson).Role, "server", StringComparison.OrdinalIgnoreCase);
}
