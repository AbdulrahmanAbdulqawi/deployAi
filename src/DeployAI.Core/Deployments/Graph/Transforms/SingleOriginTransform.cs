namespace DeployAI.Core.Deployments.Graph.Transforms;

/// <summary>
/// The single-origin shape as a graph transform: one StaticSite fronts one HttpService, the
/// site's server doubles as the reverse proxy, and the whole deployment gets exactly one
/// ingress. Replaces <see cref="SingleOriginComposeShape"/>'s hardcoded angular+dotnet gate —
/// this operates purely on capabilities and never sees a framework name.
/// </summary>
public static class SingleOriginTransform
{
    public sealed record Options(
        string? Domain = null,
        string PathPrefix = "/api",
        /// <summary>Matched to the app's own upload limit; null keeps the proxy default (usually too small).</summary>
        string? MaxBodySize = null,
        bool WebSockets = false);

    /// <summary>
    /// Applies when the graph has exactly one StaticSite and exactly one HttpService that
    /// isn't the same node. Ambiguous graphs (several APIs, no site) are left untouched —
    /// guessing which service gets the /api route is how apps land in the wrong place.
    /// </summary>
    public static bool CanApply(DeploymentGraph graph) =>
        TryPickPair(graph, out _, out _);

    public static DeploymentGraph Apply(DeploymentGraph graph, Options? options = null)
    {
        if (!TryPickPair(graph, out var site, out var api))
        {
            return graph;
        }

        options ??= new Options();

        // The fronting node takes on the proxy role; generators render its route config
        // (nginx.conf for compose) from the edges arriving at the API, not from a template.
        var frontingNode = site with { Capabilities = site.Capabilities | ServiceCapability.ReverseProxy };

        return graph
            .WithUpdatedService(frontingNode)
            .WithEdge(new ProxyRouteEdge(
                site.Id,
                api.Id,
                options.PathPrefix,
                StripPrefix: false,
                // Streaming, not buffering: large uploads should not be spooled to the proxy's
                // disk first. This was a hand-tuned line in yemenBreeze's nginx.conf.
                RequestBuffering: false,
                MaxBodySize: options.MaxBodySize,
                WebSockets: options.WebSockets))
            .WithEdge(new DependsOnEdge(site.Id, api.Id))
            .WithIngress(new IngressRule(site.Id, options.Domain));
    }

    private static bool TryPickPair(DeploymentGraph graph, out ServiceNode site, out ServiceNode api)
    {
        site = null!;
        api = null!;

        var sites = graph.WithCapability(ServiceCapability.StaticSite).ToList();
        var apis = graph.WithCapability(ServiceCapability.HttpService)
            .Where(node => !node.Capabilities.HasCapability(ServiceCapability.StaticSite))
            .ToList();

        if (sites.Count != 1 || apis.Count != 1)
        {
            return false;
        }

        site = sites[0];
        api = apis[0];
        return true;
    }
}
