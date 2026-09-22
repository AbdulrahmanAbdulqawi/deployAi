using DeployAI.Core.Deployments;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.Graph;

/// <summary>
/// Readiness for a compose deployment, decided from the graph rather than from service names.
///
/// The rule it replaces is <c>SingleOriginComposeReadinessEvaluator</c>'s "the compose file must
/// declare both an `api` and a `web` service", which is Blocking — and
/// <c>DeploymentOrchestrator</c> refuses to publish on Blocking. Any repository whose services
/// are called something else is refused for the name, not for anything wrong with it. The
/// questions worth asking are structural: can every service be built, can the proxy reach what it
/// routes to, and does anything fight Traefik for a port.
/// </summary>
public class ComposeGraphReadinessEvaluatorTests
{
    /// <summary>
    /// A service the repository can build: its Dockerfile is one that exists, which is
    /// <c>ExistingPath</c>. Content would mean the opposite — a file an adapter is able to write
    /// and nobody has written yet — and the two are what this evaluator has to tell apart.
    /// </summary>
    private static ServiceNode Built(string id, ServiceCapability capabilities, int? port = null, bool withDockerfile = true) =>
        new(
            id,
            capabilities,
            ServiceSource.FromBuild(
                id,
                withDockerfile ? new DockerfileSpec(null, "Dockerfile", port ?? 80) : null),
            Framework: withDockerfile ? "dotnet" : null,
            Ports: port is null ? [] : [port.Value]);

    private static ComposeFile Compose(params ComposeService[] services) =>
        new(services, [], IsInconclusive: false, ParseError: null);

    private static ComposeService ComposeService(string name, params string[] hostPorts) =>
        new(name, BuildContext: name, DockerfilePath: null, Image: null,
            ExposedPorts: [], HostPortMappings: hostPorts, Volumes: [], DependsOn: [],
            EnvironmentKeys: [], HasHealthcheck: false, Command: null);

    private static IReadOnlyList<MissingDeploymentFile> Evaluate(DeploymentGraph graph, ComposeFile? compose = null) =>
        ComposeGraphReadinessEvaluator.Evaluate(graph, compose ?? Compose(), "docker-compose.coolify.yml");

    private static bool Blocks(IReadOnlyList<MissingDeploymentFile> findings) =>
        findings.Any(f => f.Severity == DeploymentFileSeverity.Blocking);

    /// <summary>The case the name rule refuses: services called anything other than api and web.</summary>
    [Fact]
    public void Evaluate_AcceptsServicesWhateverTheyAreCalled()
    {
        var graph = new DeploymentGraph(
            [
                Built("storefront", ServiceCapability.StaticSite | ServiceCapability.ReverseProxy, 80),
                Built("orders", ServiceCapability.HttpService, 8080),
                Built("dispatch", ServiceCapability.Worker)
            ],
            [],
            [new IngressRule("storefront")]);

        Assert.False(Blocks(Evaluate(graph)));
    }

    /// <summary>
    /// A service DeployAI can neither build nor find a Dockerfile for cannot be deployed, and
    /// saying so by name is the difference between a fixable message and a failed build.
    /// </summary>
    [Fact]
    public void Evaluate_BlocksOnAServiceNothingCanBuild()
    {
        var graph = new DeploymentGraph(
            [Built("scraper", ServiceCapability.HttpService, 9000, withDockerfile: false)],
            [],
            []);

        var finding = Assert.Single(Evaluate(graph), f => f.Severity == DeploymentFileSeverity.Blocking);
        Assert.Contains("scraper", finding.Reason);
    }

    /// <summary>
    /// A Dockerfile DeployAI *would write* is not a Dockerfile the repository *has*. The deploy
    /// builds from what is committed, so a service still waiting for its file cannot be ready —
    /// and saying it is ready is worse than refusing, because the build fails after a green
    /// deploy. This is the case a setup run leaves behind when it writes some files and not others.
    /// </summary>
    [Fact]
    public void Evaluate_BlocksOnAServiceWhoseDockerfileHasNotBeenWrittenYet()
    {
        var graph = new DeploymentGraph(
            [
                new ServiceNode(
                    "api",
                    ServiceCapability.HttpService,
                    // Content but no ExistingPath: an adapter can write this, and nobody has.
                    ServiceSource.FromBuild("", new DockerfileSpec("FROM mcr.microsoft.com/dotnet/sdk:8.0", null, 8080)),
                    Framework: "dotnet",
                    Ports: [8080])
            ],
            [],
            []);

        var finding = Assert.Single(Evaluate(graph), f => f.Severity == DeploymentFileSeverity.Blocking);
        Assert.Contains("api", finding.Reason);
    }

    /// <summary>The same service, once the file is actually in the repository, is fine.</summary>
    [Fact]
    public void Evaluate_AcceptsAServiceWhoseDockerfileIsInTheRepository()
    {
        var graph = new DeploymentGraph(
            [
                new ServiceNode(
                    "api",
                    ServiceCapability.HttpService,
                    ServiceSource.FromBuild("", new DockerfileSpec(null, "ReelHub.Server/Dockerfile", 8080)),
                    Framework: "dotnet",
                    Ports: [8080])
            ],
            [],
            []);

        Assert.False(Blocks(Evaluate(graph)));
    }

    /// <summary>
    /// "DeployAI could not read this directory" and "this directory has no Dockerfile" are
    /// different answers, and only the second is the service's fault. Collapsing them refuses a
    /// repository that deploys perfectly well and tells its owner to add a file that is already
    /// there — which is what three live compose apps were told the first time this ran against
    /// real repositories, because the listing call it used could not return files at all.
    /// </summary>
    [Fact]
    public void Evaluate_DoesNotCallAServiceUnbuildable_WhenNobodyCouldReadItsDirectory()
    {
        var graph = new DeploymentGraph(
            [Built("scraper", ServiceCapability.HttpService, 9000, withDockerfile: false)],
            [],
            []);

        var findings = ComposeGraphReadinessEvaluator.Evaluate(
            graph, Compose(), "docker-compose.coolify.yml", unreadableDirectories: ["scraper"]);

        Assert.False(Blocks(findings));
        Assert.Contains(findings, f => f.Reason.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A pulled image needs no Dockerfile — it is not built. Demanding one would refuse every
    /// compose file that declares its own database.
    /// </summary>
    [Fact]
    public void Evaluate_DoesNotAskAPulledImageForADockerfile()
    {
        var graph = new DeploymentGraph(
            [
                new ServiceNode("db", ServiceCapability.Database, ServiceSource.FromImage("postgres:16-alpine")),
                Built("api", ServiceCapability.HttpService, 8080)
            ],
            [],
            []);

        Assert.False(Blocks(Evaluate(graph)));
    }

    /// <summary>
    /// Traefik terminates TLS and owns the host ports. A published port is the incident where the
    /// container ran, the deploy reported success, and every request got 502.
    /// </summary>
    [Fact]
    public void Evaluate_BlocksOnHostPorts_AndNamesTheServiceThatPublishesThem()
    {
        var graph = new DeploymentGraph([Built("api", ServiceCapability.HttpService, 8080)], [], []);

        var finding = Assert.Single(
            Evaluate(graph, Compose(ComposeService("api", "8080:8080"), ComposeService("web"))),
            f => f.Severity == DeploymentFileSeverity.Blocking);

        Assert.Contains("api", finding.Reason);
        Assert.DoesNotContain("web", finding.Reason);
    }

    /// <summary>
    /// Routing to a service that listens on nothing is the compose equivalent of the frozen-label
    /// incident: everything reports success and the proxy has nowhere to send traffic.
    /// </summary>
    [Fact]
    public void Evaluate_BlocksWhenIngressPointsAtAServiceWithNoPort()
    {
        var graph = new DeploymentGraph(
            [Built("web", ServiceCapability.StaticSite)],
            [],
            [new IngressRule("web")]);

        var finding = Assert.Single(Evaluate(graph), f => f.Severity == DeploymentFileSeverity.Blocking);
        Assert.Contains("web", finding.Reason);
    }

    [Fact]
    public void Evaluate_BlocksWhenIngressNamesAServiceThatIsNotThere()
    {
        var graph = new DeploymentGraph(
            [Built("web", ServiceCapability.StaticSite, 80)],
            [],
            [new IngressRule("frontend")]);

        var finding = Assert.Single(Evaluate(graph), f => f.Severity == DeploymentFileSeverity.Blocking);
        Assert.Contains("frontend", finding.Reason);
    }

    /// <summary>
    /// A deployment where nothing serves is almost certainly a mistake, but it is not DeployAI's
    /// place to refuse it — a repository of workers is a legitimate thing to run.
    /// </summary>
    [Fact]
    public void Evaluate_WarnsRatherThanBlocksWhenNothingServesTraffic()
    {
        var graph = new DeploymentGraph([Built("dispatch", ServiceCapability.Worker)], [], []);

        var findings = Evaluate(graph);

        Assert.False(Blocks(findings));
        Assert.Contains(findings, f => f.Severity != DeploymentFileSeverity.Blocking);
    }

    /// <summary>
    /// Without a healthcheck a dependency can only be waited on for "started", which is what lets
    /// an API reach a database that is up but not yet accepting connections.
    /// </summary>
    [Fact]
    public void Evaluate_RecommendsAHealthcheckOnADatabaseSomethingDependsOn()
    {
        var graph = new DeploymentGraph(
            [
                new ServiceNode("db", ServiceCapability.Database, ServiceSource.FromImage("postgres:16")),
                Built("api", ServiceCapability.HttpService, 8080)
            ],
            [new DependsOnEdge("api", "db", HealthGated: false)],
            []);

        var findings = Evaluate(graph);

        Assert.False(Blocks(findings));
        Assert.Contains(findings, f => f.Reason.Contains("db", StringComparison.Ordinal) &&
                                       f.Reason.Contains("health", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_SaysNothingWhenTheDependencyIsAlreadyHealthGated()
    {
        var graph = new DeploymentGraph(
            [
                new ServiceNode("db", ServiceCapability.Database, ServiceSource.FromImage("postgres:16")),
                Built("api", ServiceCapability.HttpService, 8080)
            ],
            [new DependsOnEdge("api", "db", HealthGated: true)],
            []);

        Assert.DoesNotContain(Evaluate(graph), f => f.Reason.Contains("health", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An empty graph means the repository declares no compose services. That is not a readiness
    /// failure — it is a different deployment shape — so this evaluator says nothing about it.
    /// </summary>
    [Fact]
    public void Evaluate_SaysNothingAboutARepositoryWithNoComposeServices()
    {
        Assert.Empty(Evaluate(DeploymentGraph.Empty));
    }
}
