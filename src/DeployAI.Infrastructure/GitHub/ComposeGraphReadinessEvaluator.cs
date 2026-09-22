using DeployAI.Core.Deployments;
using DeployAI.Core.Deployments.Graph;

namespace DeployAI.Infrastructure.GitHub;

/// <summary>
/// Readiness for a compose deployment, decided from the graph.
/// </summary>
/// <remarks>
/// <para>
/// The rule this replaces is <c>SingleOriginComposeReadinessEvaluator</c>'s "the compose file
/// must declare both an <c>api</c> and a <c>web</c> service", which is Blocking — and
/// <c>DeploymentOrchestrator</c> refuses to publish on Blocking. A repository whose services are
/// called <c>storefront</c> and <c>orders</c> is refused for its names, with nothing wrong with
/// it, and a repository with a worker or an in-compose database has no way to say so.
/// </para>
/// <para>
/// Every rule here is structural, and every Blocking one describes something that would fail
/// after a green deploy rather than during it: a service nothing can build, a proxy routing to a
/// service that listens on nothing, a container fighting Traefik for a host port. Those are the
/// failures worth refusing over, because each one reports success and then does not work.
/// </para>
/// </remarks>
public static class ComposeGraphReadinessEvaluator
{
    /// <param name="unreadableDirectories">Build contexts the scan could not list. A service in one
    /// of these is not known to be unbuildable — nobody looked — and saying otherwise turns a blind
    /// scan into a confident refusal.</param>
    public static IReadOnlyList<MissingDeploymentFile> Evaluate(
        DeploymentGraph graph,
        ComposeFile compose,
        string composePath,
        IReadOnlyCollection<string>? unreadableDirectories = null)
    {
        // No services is not a compose app. Saying nothing is the honest answer — a single
        // Dockerfile repository is a different shape, not an unready one.
        if (graph.Services.Count == 0)
        {
            return [];
        }

        var findings = new List<MissingDeploymentFile>();

        foreach (var service in graph.Services)
        {
            // A pulled image is not built, so it needs no Dockerfile; asking for one would refuse
            // every compose file that declares its own database.
            if (!service.Source.IsBuilt)
            {
                continue;
            }

            // A Dockerfile the repository has. Either the compose file named it or the directory
            // held one — either way the build has a file to use.
            if (service.Source.Dockerfile?.ExistingPath is not null)
            {
                continue;
            }

            // A Dockerfile an adapter *would* write is not one the repository has, and the build
            // uses what is committed. Reporting this as ready is worse than refusing it: the
            // deploy goes green and the build fails afterwards. It is what a setup run leaves
            // behind when it writes some of the files and not all of them.
            if (service.Source.Dockerfile?.Content is not null)
            {
                findings.Add(new MissingDeploymentFile(
                    composePath,
                    $"`{service.Id}` builds from `{Describe(service.Source.BuildContext)}`, which has no Dockerfile yet. "
                    + "DeployAI can write one — set up the deployment files and it will be included.",
                    DeploymentFileSeverity.Blocking));
                continue;
            }

            // Nobody looked, so nothing is known. Reporting it keeps the gap visible without
            // refusing a deployment on the strength of a scan that did not happen.
            if (unreadableDirectories is not null &&
                unreadableDirectories.Contains(service.Source.BuildContext ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new MissingDeploymentFile(
                    composePath,
                    $"`{service.Source.BuildContext}` could not be read, so what `{service.Id}` builds from is unknown. "
                    + "This is not a finding about the repository — the deployment may be perfectly fine.",
                    DeploymentFileSeverity.Recommended));
                continue;
            }

            findings.Add(new MissingDeploymentFile(
                composePath,
                $"`{service.Id}` builds from `{service.Source.BuildContext}`, but there is no Dockerfile there and "
                + "DeployAI did not recognise the framework, so it cannot write one. Add a Dockerfile to that "
                + "directory, or point the service at one that exists.",
                DeploymentFileSeverity.Blocking));
        }

        foreach (var service in compose.Services.Where(service => service.HostPortMappings.Count > 0))
        {
            findings.Add(new MissingDeploymentFile(
                composePath,
                $"`{service.Name}` publishes host ports ({string.Join(", ", service.HostPortMappings)}). "
                + "Coolify's Traefik terminates TLS and owns the host's ports; use `expose` instead, so the "
                + "service is reachable inside the deployment and routed from outside.",
                DeploymentFileSeverity.Blocking));
        }

        foreach (var rule in graph.Ingress)
        {
            var target = graph.FindService(rule.ServiceId);
            if (target is null)
            {
                findings.Add(new MissingDeploymentFile(
                    composePath,
                    $"The deployment routes traffic to `{rule.ServiceId}`, which is not one of its services.",
                    DeploymentFileSeverity.Blocking));
                continue;
            }

            if (target.ListenPorts.Count == 0)
            {
                findings.Add(new MissingDeploymentFile(
                    composePath,
                    $"`{rule.ServiceId}` is the service traffic is routed to, but it exposes no port, so the proxy "
                    + "has nowhere to send it. The deploy would report success and every request would fail.",
                    DeploymentFileSeverity.Blocking));
            }
        }

        if (!graph.Services.Any(service =>
                service.Capabilities.HasCapability(ServiceCapability.HttpService) ||
                service.Capabilities.HasCapability(ServiceCapability.StaticSite)))
        {
            // Not Blocking: a repository of workers is a legitimate thing to run, and refusing it
            // would be DeployAI deciding what someone's deployment is for.
            findings.Add(new MissingDeploymentFile(
                composePath,
                "None of these services serves HTTP, so the deployment will have no address to visit. "
                + "That is fine for background work, and worth checking if it was not the intent.",
                DeploymentFileSeverity.Recommended));
        }

        foreach (var edge in graph.EdgesOf<DependsOnEdge>().Where(edge => !edge.HealthGated))
        {
            var dependency = graph.FindService(edge.ToServiceId);
            if (dependency is null ||
                !(dependency.Capabilities.HasCapability(ServiceCapability.Database) ||
                  dependency.Capabilities.HasCapability(ServiceCapability.Cache)))
            {
                continue;
            }

            findings.Add(new MissingDeploymentFile(
                composePath,
                $"`{edge.FromServiceId}` waits for `{edge.ToServiceId}` to start, but `{edge.ToServiceId}` declares no "
                + "healthcheck — so it can only be waited on for \"started\", not for \"ready\". Add one, or "
                + $"`{edge.FromServiceId}` may reach it before it accepts connections.",
                DeploymentFileSeverity.Recommended));
        }

        return findings;
    }

    private static string Describe(string? buildContext) =>
        string.IsNullOrEmpty(buildContext) ? "the repository root" : buildContext;
}
