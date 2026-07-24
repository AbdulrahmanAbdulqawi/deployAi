using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data.Entities;

namespace DeployAI.Api.Services;

internal static class DeploymentTargetResolution
{
    // Role is written for every target the wizard creates, so it is the authoritative signal.
    // The provider-name fallback exists only for legacy role-less targets — and it must never
    // include Coolify: a single-origin compose app is one Coolify target, so matching it by
    // provider for BOTH website and server collapsed the two roles onto the same target and
    // then ran Railway-shaped cross-wiring against it. Coolify resolves by role or not at all.
    internal static DeploymentTarget? FindWebsiteTarget(IEnumerable<DeploymentTarget> targets)
    {
        var list = targets.ToList();
        var byRole = list.FirstOrDefault(IsWebsiteRole);
        if (byRole is not null)
        {
            return byRole;
        }

        return list.FirstOrDefault(target =>
            string.Equals(target.ProviderName, ProviderNameValues.Vercel, StringComparison.OrdinalIgnoreCase));
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
            string.Equals(target.ProviderName, ProviderNameValues.Railway, StringComparison.OrdinalIgnoreCase));
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
