namespace DeployAI.Core.Deployments.Graph;

/// <summary>
/// Which service faces the internet, and where. A single-origin deployment has exactly one of
/// these; a split-origin deployment has one per public service. Without an ingress rule the
/// service is internal-only — reachable over the service network, invisible to the proxy.
/// </summary>
public sealed record IngressRule(
    string ServiceId,
    /// <summary>Custom domain; null lets the target autogenerate one (Coolify: sslip.io).</summary>
    string? Domain = null,
    string PathPrefix = "/",
    bool Tls = true);

/// <summary>
/// The intermediate representation between detection and rendering. Transforms shape it
/// (adding proxy edges, wiring, ingress); the planner orders operations from it; generators
/// (Compose today, Kubernetes/Nomad later) render it. Nothing downstream of this graph may
/// need to know a framework name — that knowledge belongs to the adapters that built the nodes.
/// </summary>
public sealed record DeploymentGraph(
    IReadOnlyList<ServiceNode> Services,
    IReadOnlyList<ServiceEdge> Edges,
    IReadOnlyList<IngressRule> Ingress)
{
    public static DeploymentGraph Empty { get; } = new([], [], []);

    public ServiceNode? FindService(string id) =>
        Services.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ServiceNode> WithCapability(ServiceCapability capability) =>
        Services.Where(s => s.Capabilities.HasCapability(capability));

    public IEnumerable<TEdge> EdgesOf<TEdge>() where TEdge : ServiceEdge =>
        Edges.OfType<TEdge>();

    /// <summary>Edges arriving at a service — e.g. the proxy routes a ReverseProxy must render.</summary>
    public IEnumerable<TEdge> EdgesInto<TEdge>(string serviceId) where TEdge : ServiceEdge =>
        Edges.OfType<TEdge>().Where(e => string.Equals(e.ToServiceId, serviceId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<TEdge> EdgesFrom<TEdge>(string serviceId) where TEdge : ServiceEdge =>
        Edges.OfType<TEdge>().Where(e => string.Equals(e.FromServiceId, serviceId, StringComparison.OrdinalIgnoreCase));

    public DeploymentGraph WithEdge(ServiceEdge edge) =>
        this with { Edges = [.. Edges, edge] };

    public DeploymentGraph WithIngress(IngressRule rule) =>
        this with { Ingress = [.. Ingress, rule] };

    public DeploymentGraph WithService(ServiceNode node) =>
        this with { Services = [.. Services, node] };

    /// <summary>
    /// Replaces a node, keyed by id — how transforms add capabilities (e.g. marking the
    /// fronting StaticSite as also a ReverseProxy) without mutating shared state.
    /// </summary>
    public DeploymentGraph WithUpdatedService(ServiceNode node) =>
        this with
        {
            Services = Services
                .Select(s => string.Equals(s.Id, node.Id, StringComparison.OrdinalIgnoreCase) ? node : s)
                .ToArray()
        };
}
