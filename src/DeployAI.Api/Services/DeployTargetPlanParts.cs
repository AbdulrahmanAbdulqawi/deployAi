using DeployAI.Core.Deployments;
using DeployAI.Data.Entities;

namespace DeployAI.Api.Services;

/// <summary>
/// Reconstructs a project's deployment plan from the targets it was created with.
/// </summary>
/// <remarks>
/// One target is usually one plan part, with a single exception that used to be invisible. A
/// single-origin compose plan has a website part and a server part, but they deploy as one
/// application, so only one target is created — and every reader that asked "what is this project"
/// got back a lone Angular website. <see cref="SplitOriginDetection.PlanUsesSingleOriginCompose"/>
/// needs both halves, so it answered false; readiness then skipped the compose rules entirely and a
/// repository with no <c>docker-compose.coolify.yml</c> passed as ready. Expanding the target back
/// into the two parts it stands for restores what the plan already knew, rather than teaching each
/// downstream check about compose separately.
/// </remarks>
internal static class DeployTargetPlanParts
{
    internal static IReadOnlyList<DeploymentPlanPart> FromTargets(IEnumerable<DeployTarget> targets)
    {
        var parts = new List<DeploymentPlanPart>();

        foreach (var target in targets)
        {
            var config = DeployTargetConfig.Parse(target.ConfigJson);

            parts.Add(new DeploymentPlanPart(
                config.Role ?? target.ProviderName,
                target.ProviderName,
                config.RootDirectory,
                config.ServiceDirectory,
                config.BuildCommand,
                config.InstallCommand,
                config.StartCommand,
                config.OutputDirectory,
                config.Framework,
                config.DockerfilePath));

            if (config.IsComposeTarget)
            {
                parts.Add(BuildComposeServerPart(target.ProviderName, config));
            }
        }

        return parts;
    }

    /// <summary>
    /// The server half a compose target hosts alongside the website. Its directory and framework
    /// were recorded at creation precisely so this does not have to be guessed from the repository —
    /// guessing is what the wizard already did once, correctly, and what nothing persisted.
    /// </summary>
    /// <remarks>
    /// RootDirectory and ServiceDirectory here answer different questions and are not
    /// interchangeable: RootDirectory is where the api's Dockerfile has to be found (the build
    /// context), ServiceDirectory is where its source lives for config lookups. They used to be the
    /// same stored value, which was correct until a root-context multi-stage build — one project
    /// nested three directories below where its own build has to run — showed the two apart.
    /// </remarks>
    private static DeploymentPlanPart BuildComposeServerPart(string providerName, DeployTargetConfig config)
    {
        var buildRoot = config.ComposeServerRootDirectory ?? config.ComposeServerDirectory;

        return new DeploymentPlanPart(
            DeploymentPartRoles.Server,
            providerName,
            buildRoot,
            config.ComposeServerDirectory,
            Framework: config.ComposeServerFramework);
    }
}
