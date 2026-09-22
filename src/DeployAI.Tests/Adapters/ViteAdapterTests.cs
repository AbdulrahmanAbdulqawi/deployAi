using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Infrastructure.Adapters;

namespace DeployAI.Tests.Adapters;

/// <summary>
/// The adapter that makes a non-Angular front end deployable as a compose service.
///
/// Before it, a Vite repository's package.json reached only <see cref="NodeExpressAdapter"/> —
/// which declined it, or claimed it as an HttpService. Either way the graph held two HTTP services
/// and no static site, the single-origin transform refuses that pair, and the deployment had
/// nothing to front it with. That is why widening <c>SingleOriginComposeShape</c> without this
/// would have routed portfolio and tickethub to a generator that refuses them.
/// </summary>
public class ViteAdapterTests
{
    private static readonly IFrameworkAdapter[] AllAdapters =
    [
        new AngularAdapter(),
        new ViteAdapter(),
        new DotnetAdapter(),
        new NodeExpressAdapter()
    ];

    private const string VitePackageJson = """
        {
          "dependencies": { "react": "^18.3.0", "react-dom": "^18.3.0" },
          "devDependencies": { "vite": "^5.4.0" },
          "scripts": { "build": "tsc && vite build", "dev": "vite" }
        }
        """;

    private static string? WinnerFor(RepositorySignals signals) =>
        AllAdapters
            .Select(adapter => (adapter.Id, Detection: adapter.Detect(signals)))
            .Where(claim => claim.Detection is not null)
            .OrderByDescending(claim => claim.Detection!.Confidence)
            .Select(claim => claim.Id)
            .FirstOrDefault();

    [Fact]
    public void AViteRepository_IsClaimedAsAStaticSite_NotAsANodeServer()
    {
        var signals = new RepositorySignals("client", PackageJson: VitePackageJson);

        var detection = new ViteAdapter().Detect(signals);

        Assert.NotNull(detection);
        Assert.True(detection!.Capabilities.HasCapability(ServiceCapability.StaticSite));
        Assert.Equal("vite", WinnerFor(signals));
    }

    /// <summary>
    /// A framework that ships its own server is not a static bundle. Serving its build output
    /// through nginx drops the server half: the site renders and 404s its own routes.
    /// </summary>
    [Theory]
    [InlineData("next")]
    [InlineData("nuxt")]
    [InlineData("@sveltejs/kit")]
    [InlineData("astro")]
    public void AServerRenderedFramework_IsNotClaimed_EvenWhenItBuildsWithVite(string package)
    {
        var signals = new RepositorySignals(
            "client",
            PackageJson: $$"""
                { "dependencies": { "{{package}}": "^1.0.0" }, "devDependencies": { "vite": "^5.4.0" } }
                """);

        Assert.Null(new ViteAdapter().Detect(signals));
    }

    /// <summary>Angular has its own adapter and its own build layout; a file carrying both is its.</summary>
    [Fact]
    public void AnAngularRepository_IsLeftToTheAngularAdapter()
    {
        var signals = new RepositorySignals(
            "client",
            PackageJson: """
                { "dependencies": { "@angular/core": "^19.0.0" }, "devDependencies": { "vite": "^5.4.0" } }
                """);

        Assert.Null(new ViteAdapter().Detect(signals));
        Assert.Equal("angular", WinnerFor(signals));
    }

    /// <summary>
    /// Vite's default is <c>dist</c>, but a project that moves it and is copied from the default
    /// produces an image with no bundle in it — so the config is read rather than assumed.
    /// </summary>
    [Fact]
    public void TheOutputDirectoryComesFromTheViteConfig_WhenItDeclaresOne()
    {
        var node = new ViteAdapter().CreateServiceNode(
            "web",
            new RepositorySignals(
                "client",
                PackageJson: VitePackageJson,
                ViteConfig: """
                    export default defineConfig({ build: { outDir: 'dist/tickethub.client/browser' } })
                    """));

        Assert.Contains("/src/dist/tickethub.client/browser", node.Source.Dockerfile!.Content);
    }

    [Fact]
    public void TheOutputDirectoryFallsBackToVitesOwnDefault_WhenTheConfigIsSilent()
    {
        var node = new ViteAdapter().CreateServiceNode(
            "web",
            new RepositorySignals("client", PackageJson: VitePackageJson));

        Assert.Contains("/src/dist /usr/share/nginx/html", node.Source.Dockerfile!.Content);
    }

    /// <summary>
    /// The image has to carry the proxy config and listen where the compose file routes. A bundle
    /// image that does neither is the Mirqab incident: 502 from the port, 405 on login from the
    /// missing /api route, with every file individually present.
    /// </summary>
    [Fact]
    public void TheImageServesTheBundleThroughTheProxyConfig_OnThePortTheDeploymentRoutesTo()
    {
        var node = new ViteAdapter().CreateServiceNode(
            "web",
            new RepositorySignals("client", PackageJson: VitePackageJson));

        Assert.Contains("COPY nginx.conf /etc/nginx/conf.d/default.conf", node.Source.Dockerfile!.Content);
        Assert.Equal(80, node.Source.Dockerfile.ExposedPort);
        Assert.Equal([80], node.ListenPorts);
    }

    /// <summary>
    /// A repository with no build script still builds — <c>npm run build</c> would fail with
    /// "missing script", which reads like a broken image rather than a missing script.
    /// </summary>
    [Fact]
    public void ARepositoryWithNoBuildScript_IsBuiltWithViteDirectly()
    {
        var node = new ViteAdapter().CreateServiceNode(
            "client",
            new RepositorySignals(
                "client",
                PackageJson: """{ "devDependencies": { "vite": "^5.4.0" } }"""));

        Assert.Contains("npx vite build", node.Source.Dockerfile!.Content);
    }
}
