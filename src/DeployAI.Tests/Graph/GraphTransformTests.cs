using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Core.Deployments.Graph.Transforms;
using DeployAI.Infrastructure.Adapters;

namespace DeployAI.Tests.Graph;

public class SingleOriginTransformTests
{
    private static ServiceNode Site(string id = "web") => new(
        id, ServiceCapability.StaticSite, ServiceSource.FromBuild("client"), Framework: "angular");

    private static ServiceNode Api(string id = "api", string framework = "dotnet") => new(
        id, ServiceCapability.HttpService, ServiceSource.FromBuild("server"), Framework: framework, Ports: [8080]);

    [Fact]
    public void Apply_AddsProxyRouteIngressAndProxyCapability()
    {
        var graph = new DeploymentGraph([Site(), Api()], [], []);

        var result = SingleOriginTransform.Apply(graph, new SingleOriginTransform.Options(
            Domain: "breeze.example.com", MaxBodySize: "210m"));

        var route = Assert.Single(result.EdgesOf<ProxyRouteEdge>());
        Assert.Equal("web", route.FromServiceId);
        Assert.Equal("api", route.ToServiceId);
        Assert.Equal("/api", route.PathPrefix);
        Assert.False(route.RequestBuffering);
        Assert.Equal("210m", route.MaxBodySize);

        // The fronting node takes on the proxy role — capabilities compose.
        var web = result.FindService("web")!;
        Assert.True(web.Capabilities.HasCapability(ServiceCapability.StaticSite));
        Assert.True(web.Capabilities.HasCapability(ServiceCapability.ReverseProxy));

        var ingress = Assert.Single(result.Ingress);
        Assert.Equal("web", ingress.ServiceId);
        Assert.Equal("breeze.example.com", ingress.Domain);
    }

    // The transform is framework-blind: swapping the .NET api for a Node one changes nothing
    // about the shape. This is the pluggability claim, verified at the graph level.
    [Fact]
    public void Apply_IsIdenticalForANodeBackend()
    {
        var withDotnet = SingleOriginTransform.Apply(new DeploymentGraph([Site(), Api()], [], []));
        var withNode = SingleOriginTransform.Apply(
            new DeploymentGraph([Site(), Api(framework: "node-express")], [], []));

        Assert.Equal(
            withDotnet.EdgesOf<ProxyRouteEdge>().Single() with { },
            withNode.EdgesOf<ProxyRouteEdge>().Single());
        Assert.Single(withNode.Ingress);
    }

    // An SSR app is StaticSite + HttpService in one node; there is no second service to proxy
    // to, so the single-origin transform must not apply.
    [Fact]
    public void Apply_LeavesAnSsrOnlyGraphUntouched()
    {
        var ssr = new ServiceNode(
            "app",
            ServiceCapability.StaticSite | ServiceCapability.HttpService,
            ServiceSource.FromBuild("."),
            Framework: "next");
        var graph = new DeploymentGraph([ssr], [], []);

        Assert.False(SingleOriginTransform.CanApply(graph));
        Assert.Same(graph, SingleOriginTransform.Apply(graph));
    }

    // Two APIs: guessing which one gets /api is how apps land in the wrong place.
    [Fact]
    public void Apply_RefusesAmbiguousGraphs()
    {
        var graph = new DeploymentGraph([Site(), Api("api-1"), Api("api-2")], [], []);

        Assert.False(SingleOriginTransform.CanApply(graph));
        Assert.Empty(SingleOriginTransform.Apply(graph).Edges);
    }
}

public class ResourceWiringTransformTests
{
    private static readonly IFrameworkAdapterFactory Adapters = new FrameworkAdapterFactory(
        [new AngularAdapter(), new DotnetAdapter(), new NodeExpressAdapter()]);

    private static ServiceNode DotnetApi => new(
        "api", ServiceCapability.HttpService, ServiceSource.FromBuild("server"), Framework: "dotnet");

    private static ServiceNode NodeApi => new(
        "api", ServiceCapability.HttpService, ServiceSource.FromBuild("server"), Framework: "node-express");

    private static ServiceNode Postgres => new(
        "db", ServiceCapability.Database, ServiceSource.FromImage("postgres:16"));

    private static ServiceNode Bucket => new(
        "media", ServiceCapability.ObjectStorage, ServiceSource.FromImage("s3"));

    // The env keys follow the consumer's framework convention — the same bucket surfaces as
    // Storage__* on .NET and S3_* on Node. Wrong style fails silently.
    [Fact]
    public void StorageKeys_FollowTheConsumersConvention()
    {
        var dotnetEdge = ResourceWiringTransform
            .Apply(new DeploymentGraph([DotnetApi, Bucket], [], []), Adapters)
            .EdgesOf<ConnectsToEdge>().Single();
        var nodeEdge = ResourceWiringTransform
            .Apply(new DeploymentGraph([NodeApi, Bucket], [], []), Adapters)
            .EdgesOf<ConnectsToEdge>().Single();

        Assert.Contains("Storage__SecretKey", dotnetEdge.EnvMapping.Keys);
        Assert.Contains("S3_SECRET_KEY", nodeEdge.EnvMapping.Keys);
    }

    // Databases are fatal when missing; object storage silently degrades — the graph records
    // the difference so verification can treat them differently.
    [Fact]
    public void RequiredFlag_DistinguishesFatalFromDegradedWiring()
    {
        var graph = ResourceWiringTransform.Apply(
            new DeploymentGraph([DotnetApi, Postgres, Bucket], [], []), Adapters);

        var edges = graph.EdgesOf<ConnectsToEdge>().ToList();
        Assert.True(edges.Single(e => e.ToServiceId == "db").Required);
        Assert.False(edges.Single(e => e.ToServiceId == "media").Required);
    }

    [Fact]
    public void DatabaseWiring_IsHealthGated()
    {
        var graph = ResourceWiringTransform.Apply(
            new DeploymentGraph([DotnetApi, Postgres], [], []), Adapters);

        var dependsOn = Assert.Single(graph.EdgesOf<DependsOnEdge>());
        Assert.True(dependsOn.HealthGated);
    }
}
