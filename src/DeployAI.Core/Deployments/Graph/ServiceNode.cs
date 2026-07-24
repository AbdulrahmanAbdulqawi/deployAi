namespace DeployAI.Core.Deployments.Graph;

/// <summary>
/// One service in a deployment graph: a unit that is built (or pulled) and run.
/// A repository yields a *set* of these — one SSR app, or web + api, or several microservices
/// plus a worker and a database — never a hardcoded frontend/backend pair.
/// </summary>
public sealed record ServiceNode(
    /// <summary>Stable within the graph; becomes the compose service name (e.g. "web", "api").</summary>
    string Id,
    ServiceCapability Capabilities,
    ServiceSource Source,
    /// <summary>Adapter id that claimed this service ("angular", "dotnet", "node-express"); null for prebuilt images.</summary>
    string? Framework = null,
    IReadOnlyList<DetectedEnvVar>? EnvRequirements = null,
    /// <summary>Container ports this service listens on. Empty for StaticSite-only nodes served by a proxy.</summary>
    IReadOnlyList<int>? Ports = null,
    /// <summary>Path that answers 200 when healthy ("/api/health", "/health", "/up"); null when none exists.</summary>
    string? HealthPath = null,
    IReadOnlyList<VolumeMount>? Volumes = null,
    /// <summary>Override start command; null when the image's entrypoint is right.</summary>
    string? Command = null)
{
    public IReadOnlyList<DetectedEnvVar> Env => EnvRequirements ?? [];
    public IReadOnlyList<int> ListenPorts => Ports ?? [];
    public IReadOnlyList<VolumeMount> VolumeMounts => Volumes ?? [];
}

/// <summary>
/// Where a service's image comes from: built from the repo, or pulled prebuilt.
/// Exactly one of the two shapes is present.
/// </summary>
public sealed record ServiceSource(
    /// <summary>Repo-relative build context ("client", "server/YemeniBreeze.Api"); null for prebuilt images.</summary>
    string? BuildContext = null,
    /// <summary>The Dockerfile an adapter generates (or the repo provides) for this context.</summary>
    DockerfileSpec? Dockerfile = null,
    /// <summary>Registry image for provisioned resources ("postgres:16", "redis:7"); null when built.</summary>
    string? PrebuiltImage = null)
{
    public bool IsBuilt => !string.IsNullOrWhiteSpace(BuildContext);

    public static ServiceSource FromBuild(string context, DockerfileSpec? dockerfile = null) =>
        new(BuildContext: context, Dockerfile: dockerfile);

    public static ServiceSource FromImage(string image) => new(PrebuiltImage: image);
}

/// <summary>
/// What an adapter knows about the Dockerfile for a service — enough for a generator to write
/// one, without the generator knowing the framework.
/// </summary>
public sealed record DockerfileSpec(
    /// <summary>Full Dockerfile content when the adapter generates it; null when the repo has its own.</summary>
    string? Content,
    /// <summary>Context-relative path when the repo already provides a Dockerfile.</summary>
    string? ExistingPath,
    /// <summary>The port the final stage listens on (8080 for .NET, 3000 for Node, 80 for nginx).</summary>
    int ExposedPort);

public sealed record VolumeMount(
    /// <summary>Named volume ("dbdata") — generators map to the target's volume primitive.</summary>
    string Name,
    /// <summary>Mount path inside the container ("/data", "/app/wwwroot/uploads").</summary>
    string MountPath);
