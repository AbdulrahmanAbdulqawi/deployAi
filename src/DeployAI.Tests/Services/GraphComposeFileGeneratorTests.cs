using DeployAI.Api.Services;
using DeployAI.Api.Services.DeploymentTemplates;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Infrastructure.Adapters;
using DeployAI.Infrastructure.GitHub;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// The generator that replaces the Angular-plus-.NET compose templates with the graph.
///
/// What it writes is proved against the existing gate in <c>ComposeGeneratorParityTests</c>; what
/// is checked here is the part that only exists in production — reading a real repository, and
/// refusing clearly rather than writing a deployment that cannot work. Both refusals it makes are
/// for cases that would otherwise deploy green: a build context nobody could read produces
/// services with no Dockerfile, and a pair the shape does not fit produces a compose file with no
/// ingress at all.
/// </summary>
public class GraphComposeFileGeneratorTests
{
    private const string Token = "gh-token";

    private static readonly DeploymentPlanPart[] Parts =
    [
        new("website", "coolify", RootDirectory: "client", Framework: "angular"),
        new("server", "coolify", RootDirectory: "server", Framework: "dotnet")
    ];

    private static readonly MissingDeploymentFile[] Missing =
    [
        new("docker-compose.coolify.yml", "required", DeploymentFileSeverity.Blocking),
        new("client/Dockerfile", "required", DeploymentFileSeverity.Blocking),
        new("client/nginx.conf", "required", DeploymentFileSeverity.Blocking),
        new("server/Dockerfile", "required", DeploymentFileSeverity.Blocking)
    ];

    private static GraphComposeFileGenerator Generator(Mock<IGitHubService> gitHub)
    {
        var resolver = new DeploymentTemplateResolver(new DeploymentTemplateCatalog());
        return new GraphComposeFileGenerator(
            new ComposeSignalsReader(gitHub.Object, new DotnetProjectLocator(gitHub.Object)),
            new FrameworkAdapterFactory(
                [new AngularAdapter(), new ViteAdapter(), new DotnetAdapter(), new NodeExpressAdapter()]),
            new TemplateDeploymentFileGenerator(
                new DeploymentFileScaffolder(resolver),
                new DeploymentSetupFileFetcher(gitHub.Object)));
    }

    private static Mock<IGitHubService> Repository(
        Dictionary<string, string[]> listings,
        Dictionary<string, string>? files = null)
    {
        var gitHub = new Mock<IGitHubService>();

        // ListAllContentsAsync is the one that returns files; ListContentsAsync filters its result
        // to directories and can never return one. A fake that ignored the difference is how the
        // reader shipped calling the wrong method, and every test here still passed.
        gitHub.Setup(g => g.ListAllContentsAsync(Token, "acme", "app", It.IsAny<string>(), "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string? path, string? _, CancellationToken _) =>
                listings.TryGetValue(path ?? string.Empty, out var names)
                    ? names.Select(name => new GitHubContentItem(
                        name, string.IsNullOrEmpty(path) ? name : $"{path}/{name}", "file")).ToList()
                    : []);

        gitHub.Setup(g => g.ListContentsAsync(Token, "acme", "app", It.IsAny<string>(), "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        gitHub.Setup(g => g.GetFileContentAsync(Token, "acme", "app", It.IsAny<string>(), "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string path, string? _, CancellationToken _) =>
                files is not null && files.TryGetValue(path, out var content) ? content : null);

        return gitHub;
    }

    /// <summary>An Angular bundle and a .NET API in their own directories — the yemeni-breeze shape.</summary>
    private static Mock<IGitHubService> AngularAndDotnetRepository() =>
        Repository(
            new Dictionary<string, string[]>
            {
                ["client"] = ["package.json", "angular.json"],
                ["server"] = ["Breeze.Api.csproj", "appsettings.json"]
            },
            new Dictionary<string, string>
            {
                ["client/package.json"] = """{ "dependencies": { "@angular/core": "19.0.0" } }""",
                ["client/angular.json"] =
                    """{ "projects": { "client": { "architect": { "build": { "options": { "outputPath": "dist/client" } } } } } }""",
                ["server/Breeze.Api.csproj"] =
                    "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
                ["server/appsettings.json"] = """{ "ConnectionStrings": { "Default": "" } }"""
            });

    private Task<IReadOnlyList<GeneratedDeploymentFile>> GenerateAsync(
        Mock<IGitHubService> gitHub,
        IReadOnlyList<MissingDeploymentFile>? missing = null) =>
        Generator(gitHub).GenerateMissingFilesAsync(
            "acme", "app", "main", Token, Parts, missing ?? Missing, null, CancellationToken.None);

    [Fact]
    public async Task Generate_WritesTheTopologyFilesAtThePathsTheGateLooksFor()
    {
        var files = await GenerateAsync(AngularAndDotnetRepository());
        var paths = files.Select(file => file.Path).ToArray();

        Assert.Contains("docker-compose.coolify.yml", paths);
        Assert.Contains("client/Dockerfile", paths);
        Assert.Contains("client/nginx.conf", paths);
        Assert.Contains("server/Dockerfile", paths);
    }

    /// <summary>
    /// The compose file is the deployment: if it does not route traffic to the site, every request
    /// reaches a proxy with nowhere to send it, and the SPA's relative /api calls hit the static
    /// bundle instead of the API.
    /// </summary>
    [Fact]
    public async Task Generate_RoutesTheDomainToTheSiteAndProxiesApiToTheServer()
    {
        var files = await GenerateAsync(AngularAndDotnetRepository());

        var compose = files.Single(file => file.Path == "docker-compose.coolify.yml").Content;
        Assert.Contains("web:", compose, StringComparison.Ordinal);
        Assert.Contains("api:", compose, StringComparison.Ordinal);
        // A `ports:` key, not the word — the file's own header explains why there are none, and
        // matching that comment would make this assertion pass on a file that publishes them.
        Assert.DoesNotContain(
            compose.Split('\n'),
            line => line.TrimStart().StartsWith("ports:", StringComparison.Ordinal));

        var nginx = files.Single(file => file.Path == "client/nginx.conf").Content;
        Assert.Contains("proxy_pass http://api:8080;", nginx, StringComparison.Ordinal);
        Assert.Contains("try_files $uri $uri/ /index.html;", nginx, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing the graph writes is framework-shaped, but the Dockerfiles it asks adapters for are —
    /// and a client Dockerfile that never copies nginx.conf is the Mirqab incident: the proxy config
    /// is generated, nothing puts it in the image, and /api 405s on a static bundle.
    /// </summary>
    [Fact]
    public async Task Generate_PutsTheProxyConfigIntoTheImageThatServesIt()
    {
        var files = await GenerateAsync(AngularAndDotnetRepository());

        var dockerfile = files.Single(file => file.Path == "client/Dockerfile").Content;
        Assert.Contains("COPY nginx.conf", dockerfile, StringComparison.Ordinal);
        Assert.Contains("dist/client", dockerfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regenerating an unchanged repository must produce byte-identical files, or every deploy
    /// appends a commit that changes nothing to someone's history.
    /// </summary>
    [Fact]
    public async Task Generate_IsByteIdenticalOnASecondRun()
    {
        var gitHub = AngularAndDotnetRepository();

        var first = await GenerateAsync(gitHub);
        var second = await GenerateAsync(gitHub);

        Assert.Equal(
            first.Select(file => (file.Path, file.Content)),
            second.Select(file => (file.Path, file.Content)));
    }

    /// <summary>
    /// A directory nobody could read is not an empty directory. Generating from silence writes a
    /// compose file whose services have no Dockerfile: the deploy reports success and the build
    /// fails, with the plan looking correct the whole way.
    /// </summary>
    [Fact]
    public async Task Generate_RefusesWhenABuildContextCouldNotBeRead_AndNamesIt()
    {
        var gitHub = Repository(
            new Dictionary<string, string[]> { ["client"] = ["package.json", "angular.json"] });

        var error = await Assert.ThrowsAsync<DeployAIException>(() => GenerateAsync(gitHub));

        Assert.Contains("server", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two APIs and no site is not a single-origin deployment. The transform is a no-op on an
    /// ambiguous pair, so generating anyway would write a compose file with no ingress and no
    /// proxy route at all — the deployment would come up and answer nothing.
    /// </summary>
    [Fact]
    public async Task Generate_RefusesWhenTheRepositoryIsNotASiteFrontingAnApi()
    {
        var gitHub = Repository(
            new Dictionary<string, string[]>
            {
                ["client"] = ["Client.Api.csproj"],
                ["server"] = ["Breeze.Api.csproj"]
            },
            new Dictionary<string, string>
            {
                ["client/Client.Api.csproj"] = "<Project />",
                ["server/Breeze.Api.csproj"] = "<Project />"
            });

        var error = await Assert.ThrowsAsync<DeployAIException>(() => GenerateAsync(gitHub));

        Assert.Contains("one static site", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The invariant that makes widening the shape safe: a stack the classifier now routes to the
    /// compose path is one this generator can actually write files for. Widening
    /// <c>SingleOriginComposeShape</c> on its own would have sent every Vite repository — portfolio
    /// and tickethub among them — to a generator with no adapter that claims a Vite bundle, which
    /// refuses the pair for having two APIs and no site.
    /// </summary>
    [Fact]
    public async Task Generate_ProducesADeploymentForAViteFrontEnd_NowThatTheShapeAcceptsOne()
    {
        var viteParts = new DeploymentPlanPart[]
        {
            new("website", "coolify", RootDirectory: "client", Framework: "vite"),
            new("server", "coolify", RootDirectory: "server", Framework: "dotnet")
        };

        Assert.True(
            SingleOriginComposeShape.Supports("vite", "dotnet", "coolify", "coolify"),
            "The classifier no longer routes this stack here, so this test proves nothing.");

        var gitHub = Repository(
            new Dictionary<string, string[]>
            {
                ["client"] = ["package.json", "vite.config.ts"],
                ["server"] = ["Portfolio.Api.csproj"]
            },
            new Dictionary<string, string>
            {
                ["client/package.json"] =
                    """{ "dependencies": { "react": "^18.3.0" }, "devDependencies": { "vite": "^5.4.0" }, "scripts": { "build": "vite build" } }""",
                ["client/vite.config.ts"] = "export default defineConfig({ build: { outDir: 'build' } })",
                ["server/Portfolio.Api.csproj"] =
                    "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"
            });

        var files = await Generator(gitHub).GenerateMissingFilesAsync(
            "acme", "app", "main", Token, viteParts, Missing, null, CancellationToken.None);

        var paths = files.Select(file => file.Path).ToArray();
        Assert.Contains("docker-compose.coolify.yml", paths);
        Assert.Contains("client/nginx.conf", paths);
        // The directory this project's own config declares, not Vite's default.
        Assert.Contains("/src/build /usr/share/nginx/html", files.Single(f => f.Path == "client/Dockerfile").Content);
    }

    /// <summary>
    /// The graph writes topology, not prose. Dropping the documentation and health-endpoint files
    /// the template path produced would be a silent regression in what a setup PR contains, so
    /// anything the graph did not write is still asked for from the templates.
    /// </summary>
    [Fact]
    public async Task Generate_StillScaffoldsTheFilesTheGraphDoesNotWrite()
    {
        var missing = Missing
            .Append(new MissingDeploymentFile("docs/DEPLOYMENT.md", "recommended", DeploymentFileSeverity.Recommended))
            .ToArray();

        var files = await GenerateAsync(AngularAndDotnetRepository(), missing);

        Assert.Contains(files, file => file.Path == "docs/DEPLOYMENT.md");
    }
}
