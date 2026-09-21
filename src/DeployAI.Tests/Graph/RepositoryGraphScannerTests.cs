using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Infrastructure.Adapters;
using DeployAI.Infrastructure.GitHub;
using Moq;

namespace DeployAI.Tests.Graph;

/// <summary>
/// The entry point: a repository in, a <see cref="DeploymentGraph"/> out. Composes the layout
/// resolver, the compose reader, the signals reader and the graph builder, so that for the first
/// time production code can answer "what shape is this app?" without the answer being keyed to
/// Angular and .NET.
/// </summary>
public class RepositoryGraphScannerTests
{
    private const string Token = "gh-token";

    private const string ReelHubCompose = """
        services:
          db:
            image: 'postgres:16-alpine'
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
          web:
            build: ./client
            expose:
              - '80'
        """;

    private static RepositoryLayout Layout(params string[] directoriesRead) =>
        new("", "", null, [""], directoriesRead.Length == 0 ? [""] : directoriesRead);

    private static RepositoryGraphScanner CreateScanner(
        string? composeContent,
        ComposeSignalsScan? signals = null,
        RepositoryLayout? layout = null,
        string? composeFoundAt = "docker-compose.yml")
    {
        var resolver = new Mock<IRepositoryLayoutResolver>();
        resolver.Setup(r => r.ResolveAsync(Token, "acme", "app", "main", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(layout ?? Layout());

        var reader = new Mock<IRepositoryReader>();
        reader.Setup(r => r.FindAsync(Token, "acme", "app", "main", It.IsAny<RepositoryLayout>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string? _, RepositoryLayout _, string name, CancellationToken _) =>
                composeContent is not null && name == composeFoundAt
                    ? new RepositoryFile(name, composeContent)
                    : null);

        var signalsReader = new Mock<IComposeSignalsReader>();
        signalsReader.Setup(s => s.ReadAsync(Token, "acme", "app", "main", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(signals ?? new ComposeSignalsScan(
                new Dictionary<string, RepositorySignals>(StringComparer.OrdinalIgnoreCase)
                {
                    ["client"] = new RepositorySignals("client",
                        PackageJson: """{ "dependencies": { "@angular/core": "19.0.0" } }""",
                        AngularJson: """{ "projects": {} }"""),
                    ["server"] = new RepositorySignals("server",
                        CsprojContent: "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
                        CsprojFileName: "Nabdah.Api.csproj")
                },
                []));

        var adapters = new Mock<IFrameworkAdapterFactory>();
        adapters.SetupGet(a => a.All).Returns([new AngularAdapter(), new DotnetAdapter(), new NodeExpressAdapter()]);

        return new RepositoryGraphScanner(resolver.Object, reader.Object, signalsReader.Object, adapters.Object);
    }

    private static Task<RepositoryGraphScan> ScanAsync(RepositoryGraphScanner scanner) =>
        scanner.ScanAsync(Token, "acme", "app", "main", null, CancellationToken.None);

    [Fact]
    public async Task ScanAsync_ProducesAGraphOfEveryServiceTheRepositoryDeclares()
    {
        var scan = await ScanAsync(CreateScanner(ReelHubCompose));

        Assert.False(scan.IsInconclusive);
        Assert.Equal("docker-compose.yml", scan.ComposePath);
        Assert.Equal(["db", "api", "worker", "web"], scan.Graph.Services.Select(s => s.Id));
        Assert.True(scan.Graph.FindService("db")!.Capabilities.HasCapability(ServiceCapability.Database));
        Assert.True(scan.Graph.FindService("worker")!.Capabilities.HasCapability(ServiceCapability.Worker));
        Assert.Equal("angular", scan.Graph.FindService("web")!.Framework);
    }

    /// <summary>
    /// The generated file wins over the repository's own dev stack. `docker-compose.yml` in most
    /// repos is the local development stack, which publishes host ports Traefik expects to own.
    /// </summary>
    [Fact]
    public async Task ScanAsync_PrefersTheCoolifyComposeFileOverTheDevelopmentOne()
    {
        var scanner = CreateScanner(ReelHubCompose, composeFoundAt: "docker-compose.coolify.yml");

        var scan = await ScanAsync(scanner);

        Assert.Equal("docker-compose.coolify.yml", scan.ComposePath);
    }

    /// <summary>
    /// A repository with no compose file is a conclusive answer, not a failure: plenty of apps
    /// are a single Dockerfile. The caller needs to tell that apart from a scan that went blind.
    /// </summary>
    [Fact]
    public async Task ScanAsync_NoComposeFileIsConclusiveAndEmpty()
    {
        var scan = await ScanAsync(CreateScanner(composeContent: null));

        Assert.False(scan.IsInconclusive);
        Assert.Null(scan.ComposePath);
        Assert.Empty(scan.Graph.Services);
    }

    /// <summary>
    /// A layout that listed no directories means the repository could not be read at all. Saying
    /// "no compose file" about a repository nobody could open is the absence bug exactly.
    /// </summary>
    [Fact]
    public async Task ScanAsync_IsInconclusiveWhenTheRepositoryCouldNotBeListed()
    {
        var blind = new RepositoryLayout("", "", null, [""], []);

        var scan = await ScanAsync(CreateScanner(composeContent: null, layout: blind));

        Assert.True(scan.IsInconclusive);
        Assert.Contains("could not", scan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanAsync_IsInconclusiveWhenTheComposeFileCouldNotBeParsed()
    {
        var scan = await ScanAsync(CreateScanner("services:\n  api:\n   build: ./\n  \t- broken: [unclosed\n"));

        Assert.True(scan.IsInconclusive);
        Assert.Empty(scan.Graph.Services);
        Assert.False(string.IsNullOrWhiteSpace(scan.Reason));
    }

    /// <summary>
    /// A build context that could not be read is carried through. The graph still holds the
    /// service — it exists — but the caller can say which part of the answer is unreliable.
    /// </summary>
    [Fact]
    public async Task ScanAsync_CarriesUnreadableBuildContextsThrough()
    {
        var scanner = CreateScanner(
            ReelHubCompose,
            new ComposeSignalsScan(new Dictionary<string, RepositorySignals>(StringComparer.OrdinalIgnoreCase), ["server", "client"]));

        var scan = await ScanAsync(scanner);

        Assert.Equal(["server", "client"], scan.UnreadableDirectories);
        // The services are still there; only their frameworks are unknown.
        Assert.Equal(4, scan.Graph.Services.Count);
        Assert.Null(scan.Graph.FindService("api")!.Framework);
    }

    /// <summary>The signals reader is asked for the contexts the compose file names, and no others.</summary>
    [Fact]
    public async Task ScanAsync_AsksOnlyForTheBuildContextsTheComposeFileNames()
    {
        var signalsReader = new Mock<IComposeSignalsReader>();
        IReadOnlyCollection<string>? requested = null;
        signalsReader.Setup(s => s.ReadAsync(Token, "acme", "app", "main", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? _, IReadOnlyCollection<string> dirs, CancellationToken _) => requested = dirs)
            .ReturnsAsync(new ComposeSignalsScan(new Dictionary<string, RepositorySignals>(StringComparer.OrdinalIgnoreCase), []));

        var resolver = new Mock<IRepositoryLayoutResolver>();
        resolver.Setup(r => r.ResolveAsync(Token, "acme", "app", "main", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Layout());
        var reader = new Mock<IRepositoryReader>();
        reader.Setup(r => r.FindAsync(Token, "acme", "app", "main", It.IsAny<RepositoryLayout>(), "docker-compose.yml", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryFile("docker-compose.yml", ReelHubCompose));
        reader.Setup(r => r.FindAsync(Token, "acme", "app", "main", It.IsAny<RepositoryLayout>(), It.Is<string>(n => n != "docker-compose.yml"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RepositoryFile?)null);
        var adapters = new Mock<IFrameworkAdapterFactory>();
        adapters.SetupGet(a => a.All).Returns([]);

        await ScanAsync(new RepositoryGraphScanner(resolver.Object, reader.Object, signalsReader.Object, adapters.Object));

        // `db` is a pulled image and has no build context; `api` and `worker` share one.
        Assert.Equal(["server", "client"], requested!.OrderByDescending(d => d).ToList());
    }
}
