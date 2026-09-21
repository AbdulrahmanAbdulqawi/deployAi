using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;

namespace DeployAI.Infrastructure.GitHub;

/// <summary>
/// Builds a <see cref="DeploymentGraph"/> from a repository's own compose file.
/// </summary>
/// <remarks>
/// <para>
/// This is the link that was missing. <see cref="DeploymentGraph"/>, its transforms,
/// <c>ComposeGenerator</c> and <c>CoolifyComposePlanner</c> were written, tested, and reachable
/// from nothing, because no production code ever built a graph — so the framework-agnostic
/// deployment path existed only in its own tests while the shipping path stayed keyed to
/// Angular + .NET.
/// </para>
/// <para>
/// Deliberately pure: the caller assembles <see cref="RepositorySignals"/> for each build context
/// (which is I/O) and hands them in. That keeps the rules here testable against a compose file
/// and a dictionary, and it is why the reel-hub shape could be pinned before any of it ran
/// against GitHub.
/// </para>
/// </remarks>
public static class ComposeGraphBuilder
{
    /// <summary>Images whose presence names the service, since no adapter inspects a pulled image.</summary>
    private static readonly (string Fragment, ServiceCapability Capability)[] KnownImages =
    [
        ("postgres", ServiceCapability.Database),
        ("mysql", ServiceCapability.Database),
        ("mariadb", ServiceCapability.Database),
        ("mongo", ServiceCapability.Database),
        ("redis", ServiceCapability.Cache),
        ("keydb", ServiceCapability.Cache),
        ("valkey", ServiceCapability.Cache),
        ("memcached", ServiceCapability.Cache)
    ];

    public static DeploymentGraph Build(
        ComposeFile compose,
        IReadOnlyDictionary<string, RepositorySignals> signalsByDirectory,
        IReadOnlyList<IFrameworkAdapter> adapters)
    {
        // A file that could not be read describes nothing. The caller checks IsInconclusive and
        // says so; returning a confident empty graph here would be the absence bug in miniature.
        if (compose.IsInconclusive)
        {
            return DeploymentGraph.Empty;
        }

        var graph = DeploymentGraph.Empty;
        foreach (var service in compose.Services)
        {
            graph = graph.WithService(BuildNode(service, signalsByDirectory, adapters));
        }

        foreach (var service in compose.Services)
        {
            foreach (var dependency in service.DependsOn)
            {
                var target = compose.Services.FirstOrDefault(s =>
                    string.Equals(s.Name, dependency, StringComparison.OrdinalIgnoreCase));

                // A dependency that declares a healthcheck can be waited on properly; one that
                // does not can only be waited on for "started", which is what lets an API reach a
                // database that is up but not yet accepting connections.
                graph = graph.WithEdge(new DependsOnEdge(
                    service.Name,
                    dependency,
                    HealthGated: target?.HasHealthcheck ?? false));
            }
        }

        return graph;
    }

    private static ServiceNode BuildNode(
        ComposeService service,
        IReadOnlyDictionary<string, RepositorySignals> signalsByDirectory,
        IReadOnlyList<IFrameworkAdapter> adapters)
    {
        var volumes = service.Volumes
            .Select(volume => new VolumeMount(volume.Name, volume.MountPath))
            .ToList();

        if (!string.IsNullOrWhiteSpace(service.Image) && string.IsNullOrWhiteSpace(service.BuildContext))
        {
            return new ServiceNode(
                service.Name,
                CapabilityForImage(service.Image),
                ServiceSource.FromImage(service.Image),
                Ports: service.ExposedPorts,
                Volumes: volumes,
                Command: service.Command);
        }

        var directory = NormalizeContext(service.BuildContext);
        signalsByDirectory.TryGetValue(directory, out var signals);
        signals ??= new RepositorySignals(directory);

        var claim = adapters
            .Select(adapter => (Adapter: adapter, Detection: adapter.Detect(signals)))
            .Where(candidate => candidate.Detection is not null)
            .OrderByDescending(candidate => candidate.Detection!.Confidence)
            .FirstOrDefault();

        var node = claim.Adapter is not null
            ? claim.Adapter.CreateServiceNode(service.Name, signals)
            // Nothing claimed it, and the service still exists. A node with no framework builds
            // from its own context; what it cannot get is a generated Dockerfile, which is a
            // separate answer from "this service is not here".
            : new ServiceNode(
                service.Name,
                ServiceCapability.HttpService,
                ServiceSource.FromBuild(directory));

        // The compose file is evidence about *this service*; the adapter only ever saw a
        // directory, and two services can build from one directory (api and worker do).
        var capabilities = ResolveCapabilities(service, node.Capabilities);

        return node with
        {
            Capabilities = capabilities,
            // A worker takes no inbound traffic, so it keeps no port even though the adapter that
            // built the node offered one: the generator would otherwise expose 8080 on a service
            // that never listens, and the deployment would look like it had two APIs.
            Ports = service.ExposedPorts.Count > 0
                ? service.ExposedPorts
                : capabilities.HasCapability(ServiceCapability.Worker) ? [] : node.ListenPorts,
            Volumes = volumes.Count > 0 ? volumes : node.VolumeMounts,
            Command = service.Command ?? node.Command,
            Source = node.Source with { BuildContext = directory }
        };
    }

    /// <summary>
    /// A built service that exposes no port and carries its own command is not serving traffic,
    /// whatever the adapter concluded from the directory. reel-hub builds its API and its worker
    /// from the same csproj, so directory-level detection cannot tell them apart and the compose
    /// file can.
    /// </summary>
    private static ServiceCapability ResolveCapabilities(ComposeService service, ServiceCapability detected) =>
        service.ExposedPorts.Count == 0 && !string.IsNullOrWhiteSpace(service.Command)
            ? ServiceCapability.Worker
            : detected;

    private static ServiceCapability CapabilityForImage(string image)
    {
        foreach (var (fragment, capability) in KnownImages)
        {
            if (image.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return capability;
            }
        }

        // An unrecognised image is still something that runs and may answer requests. Guessing
        // HttpService keeps it deployable; guessing nothing would drop it from every loop.
        return ServiceCapability.HttpService;
    }

    /// <summary>
    /// Compose contexts are written relative to the compose file (<c>./server</c>, <c>./</c>);
    /// every layout in DeployAI is repository-relative with no leading or trailing slash.
    /// </summary>
    /// <remarks>
    /// Public because the caller that fetches signals has to key its dictionary by exactly what
    /// this produces. Two copies of the rule would mean the dictionary is filled under one
    /// spelling and read under another, and every service would silently lose its framework —
    /// a failure that looks like "no adapter claimed it" rather than like a mismatch.
    /// </remarks>
    public static string NormalizeContext(string? context)
    {
        var trimmed = (context ?? string.Empty).Trim().Replace('\\', '/');
        if (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Trim('/') == "." ? string.Empty : trimmed.Trim('/');
    }
}
