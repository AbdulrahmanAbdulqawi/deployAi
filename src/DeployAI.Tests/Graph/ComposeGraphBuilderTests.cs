using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Infrastructure.Adapters;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.Graph;

/// <summary>
/// Turning a parsed compose file into a <see cref="DeploymentGraph"/>. This is the link that has
/// been missing: the graph, its transforms, the compose generator and the Coolify planner are all
/// written and tested, and nothing has ever built a graph from a repository, so none of it runs.
///
/// The shape under test is reel-hub's, because it is the one nothing today can express: four
/// services, one of them a worker, one of them a database living *inside* the compose file rather
/// than provisioned on the provider.
/// </summary>
public class ComposeGraphBuilderTests
{
    private const string AngularPackageJson = """
        { "dependencies": { "@angular/core": "19.0.0" }, "scripts": { "build": "npm run build" } }
        """;

    private const string AngularJson = """
        { "projects": { "client": { "architect": { "build": { "options": { "outputPath": "dist/client" } } } } } }
        """;

    private const string Csproj =
        "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";

    private const string ReelHubCompose = """
        services:
          db:
            image: 'postgres:16-alpine'
            environment:
              POSTGRES_DB: '${POSTGRES_DB:-nabdah}'
            volumes:
              - 'nabdah-db-data:/var/lib/postgresql/data'
            healthcheck:
              test: ['CMD-SHELL', 'pg_isready']
          api:
            build: ./server
            expose:
              - '8080'
            depends_on:
              - db
          worker:
            build: ./server
            command: dotnet Nabdah.Worker.dll
            depends_on:
              - db
          web:
            build: ./client
            expose:
              - '80'
            depends_on:
              - api
        """;

    private static IReadOnlyList<IFrameworkAdapter> Adapters =>
        [new AngularAdapter(), new DotnetAdapter(), new NodeExpressAdapter()];

    private static Dictionary<string, RepositorySignals> ReelHubSignals() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["client"] = new RepositorySignals("client", PackageJson: AngularPackageJson, AngularJson: AngularJson),
        ["server"] = new RepositorySignals("server", CsprojContent: Csproj, CsprojFileName: "Nabdah.Api.csproj")
    };

    private static DeploymentGraph BuildReelHub() =>
        ComposeGraphBuilder.Build(ComposeFileReader.Read(ReelHubCompose), ReelHubSignals(), Adapters);

    [Fact]
    public void Build_CreatesANodePerComposeService()
    {
        var graph = BuildReelHub();

        Assert.Equal(["db", "api", "worker", "web"], graph.Services.Select(s => s.Id));
    }

    /// <summary>
    /// A postgres image declared as a compose service is a Database node in the graph — not a
    /// request to provision one on the provider, which is what the image regex means today.
    /// </summary>
    [Fact]
    public void Build_TreatsAnInComposeDatabaseAsADatabaseNode()
    {
        var db = BuildReelHub().FindService("db")!;

        Assert.True(db.Capabilities.HasCapability(ServiceCapability.Database));
        Assert.False(db.Capabilities.IsDeployable());
        Assert.Equal("postgres:16-alpine", db.Source.PrebuiltImage);
        Assert.False(db.Source.IsBuilt);
        Assert.Null(db.Framework);

        var volume = Assert.Single(db.VolumeMounts);
        Assert.Equal("nabdah-db-data", volume.Name);
        Assert.Equal("/var/lib/postgresql/data", volume.MountPath);
    }

    [Fact]
    public void Build_RecognisesACacheImageSeparatelyFromADatabase()
    {
        var graph = ComposeGraphBuilder.Build(
            ComposeFileReader.Read("""
                services:
                  cache:
                    image: 'redis:7-alpine'
                """),
            new Dictionary<string, RepositorySignals>(),
            Adapters);

        var cache = graph.FindService("cache")!;
        Assert.True(cache.Capabilities.HasCapability(ServiceCapability.Cache));
        Assert.False(cache.Capabilities.HasCapability(ServiceCapability.Database));
    }

    /// <summary>
    /// The adapter claims HttpService for any csproj. A service built from the same directory but
    /// exposing no port and carrying its own command is not serving anything — the compose file is
    /// the evidence, and it outranks the adapter's directory-level guess.
    /// </summary>
    [Fact]
    public void Build_ClassifiesABuiltServiceWithNoPortAndItsOwnCommandAsAWorker()
    {
        var graph = BuildReelHub();

        var worker = graph.FindService("worker")!;
        Assert.True(worker.Capabilities.HasCapability(ServiceCapability.Worker));
        Assert.False(worker.Capabilities.HasCapability(ServiceCapability.HttpService));
        Assert.Equal("dotnet Nabdah.Worker.dll", worker.Command);
        Assert.Equal("dotnet", worker.Framework);

        // Same directory, same adapter, different service: the API keeps its HttpService claim.
        var api = graph.FindService("api")!;
        Assert.True(api.Capabilities.HasCapability(ServiceCapability.HttpService));
        Assert.False(api.Capabilities.HasCapability(ServiceCapability.Worker));
    }

    [Fact]
    public void Build_AsksEveryAdapterAndTakesTheStrongestClaim()
    {
        var graph = BuildReelHub();

        // client has both @angular/core and a package.json the Node adapter could claim.
        Assert.Equal("angular", graph.FindService("web")!.Framework);
        Assert.Equal("dotnet", graph.FindService("api")!.Framework);
    }

    [Fact]
    public void Build_TakesPortsFromTheComposeFileRatherThanTheAdapterDefault()
    {
        var graph = BuildReelHub();

        Assert.Equal([8080], graph.FindService("api")!.ListenPorts);
        Assert.Equal([80], graph.FindService("web")!.ListenPorts);
        Assert.Empty(graph.FindService("worker")!.ListenPorts);
    }

    /// <summary>
    /// `depends_on` becomes an edge, and a dependency that declares a healthcheck becomes a
    /// health-gated one — the difference between "started" and "ready", which is what stops an
    /// API racing an empty database.
    /// </summary>
    [Fact]
    public void Build_TurnsDependenciesIntoEdges_HealthGatedWhenTheTargetHasAHealthcheck()
    {
        var graph = BuildReelHub();

        var edges = graph.EdgesOf<DependsOnEdge>().ToList();
        Assert.Equal(3, edges.Count);

        var apiOnDb = Assert.Single(edges, e => e.FromServiceId == "api" && e.ToServiceId == "db");
        Assert.True(apiOnDb.HealthGated);

        var webOnApi = Assert.Single(edges, e => e.FromServiceId == "web" && e.ToServiceId == "api");
        Assert.False(webOnApi.HealthGated);
    }

    /// <summary>
    /// A build context nothing claims still becomes a node. Dropping it would make the graph
    /// describe fewer services than the repository declares, which is the failure this whole
    /// layer exists to stop.
    /// </summary>
    [Fact]
    public void Build_KeepsAServiceNoAdapterClaims()
    {
        var graph = ComposeGraphBuilder.Build(
            ComposeFileReader.Read("""
                services:
                  scraper:
                    build: ./tools/scraper
                    expose:
                      - '9000'
                """),
            new Dictionary<string, RepositorySignals>(StringComparer.OrdinalIgnoreCase)
            {
                ["tools/scraper"] = new RepositorySignals("tools/scraper", GoMod: "module scraper")
            },
            Adapters);

        var scraper = graph.FindService("scraper")!;
        Assert.Null(scraper.Framework);
        Assert.True(scraper.Capabilities.HasCapability(ServiceCapability.HttpService));
        Assert.Equal("tools/scraper", scraper.Source.BuildContext);
    }

    [Theory]
    [InlineData("./server", "server")]
    [InlineData("./", "")]
    [InlineData(".", "")]
    [InlineData("server/Api/", "server/Api")]
    public void Build_NormalisesTheBuildContextToARepositoryRelativeDirectory(string context, string expected)
    {
        var graph = ComposeGraphBuilder.Build(
            ComposeFileReader.Read($"services:\n  api:\n    build: '{context}'\n"),
            new Dictionary<string, RepositorySignals>(),
            Adapters);

        Assert.Equal(expected, graph.FindService("api")!.Source.BuildContext);
    }

    /// <summary>
    /// Which services face the internet is the user's choice — Coolify offers a domain for every
    /// service in a compose app — so the builder states no ingress and leaves it to be chosen.
    /// </summary>
    [Fact]
    public void Build_AssignsNoIngress()
    {
        Assert.Empty(BuildReelHub().Ingress);
    }

    [Fact]
    public void Build_ReturnsAnEmptyGraphForAComposeFileItCouldNotParse()
    {
        var graph = ComposeGraphBuilder.Build(
            ComposeFileReader.Read("services:\n  api:\n   build: ./\n  \t- broken: [unclosed\n"),
            new Dictionary<string, RepositorySignals>(),
            Adapters);

        Assert.Empty(graph.Services);
        Assert.Empty(graph.Edges);
    }
}
