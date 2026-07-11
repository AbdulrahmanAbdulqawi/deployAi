using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data.Entities;

namespace DeployAI.Api.Services;

internal static class DeploymentTargetResolution
{
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

    internal static bool IsCoolifyFullStack(IEnumerable<DeploymentTarget> targets)
    {
        var website = FindWebsiteTarget(targets);
        var server = FindServerTarget(targets);
        return website is not null &&
               server is not null &&
               string.Equals(website.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(server.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWebsiteRole(DeploymentTarget target) =>
        string.Equals(ResolveRole(target), "website", StringComparison.OrdinalIgnoreCase);

    private static bool IsServerRole(DeploymentTarget target) =>
        string.Equals(ResolveRole(target), "server", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveRole(DeploymentTarget target) =>
        DeployTargetConfig.Parse(target.DeployTarget.ConfigJson).Role;
}
