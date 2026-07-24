namespace DeployAI.Core.Deployments.Graph;

/// <summary>
/// What a service *is*, independent of the framework that realizes it. The deployment engine
/// reasons about capabilities; frameworks (Angular, .NET, Node, FastAPI…) are metadata on how
/// a capability is built. This generalizes <see cref="DeploymentPartRoles"/>' flat role strings.
///
/// A service holds a *set* of these, not one: an SSR app is StaticSite + HttpService, a Django
/// app with inline Celery is HttpService + Worker, and the nginx of a single-origin deployment
/// is StaticSite + ReverseProxy. Transforms match on membership — never on "the frontend".
/// </summary>
[Flags]
public enum ServiceCapability
{
    None = 0,

    /// <summary>Serves prebuilt static assets (an SPA bundle, a docs site).</summary>
    StaticSite = 1 << 0,

    /// <summary>Listens for HTTP requests at runtime (an API, an SSR server).</summary>
    HttpService = 1 << 1,

    /// <summary>Runs background jobs; no inbound traffic.</summary>
    Worker = 1 << 2,

    /// <summary>A database engine (Postgres, MySQL, SQLite-in-a-volume…).</summary>
    Database = 1 << 3,

    /// <summary>A cache/broker (Redis, Memcached…).</summary>
    Cache = 1 << 4,

    /// <summary>An S3-compatible bucket. Provisioned, never deployed.</summary>
    ObjectStorage = 1 << 5,

    /// <summary>Forwards traffic to other services (the nginx in front of an API).</summary>
    ReverseProxy = 1 << 6
}

public static class ServiceCapabilityExtensions
{
    public static bool HasCapability(this ServiceCapability capabilities, ServiceCapability wanted) =>
        (capabilities & wanted) == wanted && wanted != ServiceCapability.None;

    /// <summary>
    /// True when the service is something a deploy target builds and runs — as opposed to
    /// provisioned resources (databases, caches, buckets), which have no build step and must
    /// stay out of deploy loops and progress bars.
    /// </summary>
    public static bool IsDeployable(this ServiceCapability capabilities) =>
        capabilities.HasCapability(ServiceCapability.StaticSite) ||
        capabilities.HasCapability(ServiceCapability.HttpService) ||
        capabilities.HasCapability(ServiceCapability.Worker) ||
        capabilities.HasCapability(ServiceCapability.ReverseProxy);
}
