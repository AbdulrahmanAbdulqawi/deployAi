using DeployAI.Infrastructure.GitHub;
using Moq;

namespace DeployAI.Tests.Graph;

/// <summary>
/// Fetching the per-directory evidence the graph builder needs. This is the I/O half that
/// <see cref="ComposeGraphBuilder"/> deliberately does not do: the builder takes a dictionary of
/// signals, and this is what fills it from a repository.
///
/// The cost matters. A compose file names one build context per built service, and asking GitHub
/// for every file an adapter might want, in every context, is a request per file per service. So
/// each directory is listed once and only the files that exist are fetched.
/// </summary>
public class ComposeSignalsReaderTests
{
    private const string Token = "gh-token";

    /// <summary>
    /// The two listing methods behave as the real service does, and they are not the same:
    /// <c>ListContentsAsync</c> filters its result to <c>type == "dir"</c> and never returns a
    /// file, while <c>ListAllContentsAsync</c> returns everything. A fake that served files from
    /// the first one let every test here pass against a reader that, in production, saw nothing at
    /// all — which is exactly what happened: three deployed compose apps were told their build
    /// contexts had no Dockerfile.
    /// </summary>
    private static Mock<IGitHubService> GitHubWith(
        Dictionary<string, string[]> listings,
        Dictionary<string, string>? files = null)
    {
        var gitHub = new Mock<IGitHubService>(MockBehavior.Strict);

        gitHub.Setup(g => g.ListAllContentsAsync(Token, "acme", "app", It.IsAny<string>(), "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string? path, string? _, CancellationToken _) =>
                listings.TryGetValue(path ?? string.Empty, out var names)
                    ? names.Select(name => new GitHubContentItem(name, string.IsNullOrEmpty(path) ? name : $"{path}/{name}", "file")).ToList()
                    : []);

        gitHub.Setup(g => g.ListContentsAsync(Token, "acme", "app", It.IsAny<string>(), "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string? path, string? _, CancellationToken _) =>
                listings.TryGetValue(path ?? string.Empty, out var names)
                    ? names.Where(IsDirectoryName)
                        .Select(name => new GitHubContentItem(name, string.IsNullOrEmpty(path) ? name : $"{path}/{name}", "dir"))
                        .ToList()
                    : []);

        gitHub.Setup(g => g.GetFileContentAsync(Token, "acme", "app", It.IsAny<string>(), "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string path, string? _, CancellationToken _) =>
                files is not null && files.TryGetValue(path, out var content) ? content : null);

        return gitHub;
    }

    /// <summary>Fixture convention: an entry with no extension and no known filename is a folder.</summary>
    private static bool IsDirectoryName(string name) =>
        !name.Contains('.', StringComparison.Ordinal) &&
        !name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase);

    private static ComposeSignalsReader Reader(Mock<IGitHubService> gitHub) =>
        new(gitHub.Object, new DotnetProjectLocator(gitHub.Object));

    [Fact]
    public async Task ReadAsync_CollectsTheFilesAnAdapterNeeds_PerBuildContext()
    {
        var gitHub = GitHubWith(
            new Dictionary<string, string[]>
            {
                ["client"] = ["package.json", "angular.json", "src"],
                ["server"] = ["Nabdah.Api.csproj", "Dockerfile", "appsettings.json"]
            },
            new Dictionary<string, string>
            {
                ["client/package.json"] = """{ "dependencies": { "@angular/core": "19.0.0" } }""",
                ["client/angular.json"] = """{ "projects": {} }""",
                ["server/Nabdah.Api.csproj"] = "<Project />",
                ["server/Dockerfile"] = "FROM x",
                ["server/appsettings.json"] = """{ "ConnectionStrings": {} }"""
            });

        var scan = await Reader(gitHub).ReadAsync(Token, "acme", "app", "main", ["client", "server"], CancellationToken.None);

        var client = scan.SignalsByDirectory["client"];
        Assert.Contains("@angular/core", client.PackageJson);
        Assert.NotNull(client.AngularJson);
        Assert.False(client.HasDockerfile);

        var server = scan.SignalsByDirectory["server"];
        Assert.Equal("Nabdah.Api.csproj", server.CsprojFileName);
        Assert.Equal("<Project />", server.CsprojContent);
        Assert.True(server.HasDockerfile);
        Assert.NotNull(server.AppsettingsJson);
        Assert.Null(server.PackageJson);
        Assert.Empty(scan.UnreadableDirectories);
    }

    /// <summary>
    /// The listing is what decides which files to fetch. Asking for files the directory does not
    /// contain would be a request each, per service, on every scan.
    /// </summary>
    [Fact]
    public async Task ReadAsync_FetchesOnlyFilesTheListingShowed()
    {
        var gitHub = GitHubWith(
            new Dictionary<string, string[]> { ["server"] = ["go.mod"] },
            new Dictionary<string, string> { ["server/go.mod"] = "module app" });

        var scan = await Reader(gitHub).ReadAsync(Token, "acme", "app", "main", ["server"], CancellationToken.None);

        Assert.Equal("module app", scan.SignalsByDirectory["server"].GoMod);
        gitHub.Verify(g => g.GetFileContentAsync(Token, "acme", "app", "server/go.mod", "main", It.IsAny<CancellationToken>()), Times.Once);
        gitHub.Verify(g => g.GetFileContentAsync(Token, "acme", "app", "server/package.json", "main", It.IsAny<CancellationToken>()), Times.Never);
        gitHub.Verify(g => g.GetFileContentAsync(Token, "acme", "app", "server/Dockerfile", "main", It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A build context that lists nothing is reported, not silently given empty signals. Empty
    /// signals mean "this directory holds nothing an adapter recognises", and no adapter would
    /// claim it — so the service would be built with no framework for a reason that is not true.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ReportsADirectoryItCouldNotRead_RatherThanInventingEmptySignals()
    {
        var gitHub = GitHubWith(new Dictionary<string, string[]> { ["client"] = ["package.json"] },
            new Dictionary<string, string> { ["client/package.json"] = "{}" });

        var scan = await Reader(gitHub).ReadAsync(Token, "acme", "app", "main", ["client", "server"], CancellationToken.None);

        Assert.Equal(["server"], scan.UnreadableDirectories);
        Assert.False(scan.SignalsByDirectory.ContainsKey("server"));
        Assert.True(scan.SignalsByDirectory.ContainsKey("client"));
    }

    private const string Solution = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "TicketHub.Domain", "TicketHub.Domain\TicketHub.Domain.csproj", "{A1}"
        EndProject
        Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "TicketHub.Server", "TicketHub.Server\TicketHub.Server.csproj", "{A2}"
        EndProject
        Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "TicketHub.Tests", "TicketHub.Tests\TicketHub.Tests.csproj", "{A3}"
        EndProject
        """;

    /// <summary>
    /// A build context whose project file is somewhere inside it, rather than in it.
    ///
    /// An API that references sibling projects has to build from the directory that holds them
    /// all, and that directory has no csproj — so nothing recognised it, nothing generated a
    /// Dockerfile, and a real setup run produced three of the four files the deployment needed.
    /// The solution file is what says which projects exist; reading them is what says which one
    /// is the app.
    /// </summary>
    [Fact]
    public async Task ReadAsync_FindsTheAppProjectThroughTheSolution_WhenTheContextHasNoProjectFile()
    {
        var gitHub = GitHubWith(
            new Dictionary<string, string[]> { [""] = ["TicketHub.sln", ".dockerignore"] },
            new Dictionary<string, string>
            {
                ["TicketHub.sln"] = Solution,
                ["TicketHub.Domain/TicketHub.Domain.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                ["TicketHub.Server/TicketHub.Server.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />",
                ["TicketHub.Tests/TicketHub.Tests.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />"
            });

        var scan = await Reader(gitHub).ReadAsync(Token, "acme", "app", "main", [""], CancellationToken.None);

        var root = scan.SignalsByDirectory[""];
        Assert.Equal("TicketHub.Server/TicketHub.Server.csproj", root.ProjectFilePath);
        Assert.Equal("TicketHub.Server.csproj", root.CsprojFileName);
        Assert.Contains("Microsoft.NET.Sdk.Web", root.CsprojContent);
    }

    /// <summary>
    /// A solution of libraries is not an app. Picking one anyway produces an image with no entry
    /// point — the failure the csproj-ranking rule already exists to prevent one level down.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ClaimsNoProject_WhenTheSolutionHoldsNoRunnableApp()
    {
        var gitHub = GitHubWith(
            new Dictionary<string, string[]> { [""] = ["TicketHub.sln"] },
            new Dictionary<string, string>
            {
                ["TicketHub.sln"] = Solution,
                ["TicketHub.Domain/TicketHub.Domain.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                ["TicketHub.Server/TicketHub.Server.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                ["TicketHub.Tests/TicketHub.Tests.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />"
            });

        var scan = await Reader(gitHub).ReadAsync(Token, "acme", "app", "main", [""], CancellationToken.None);

        Assert.Null(scan.SignalsByDirectory[""].ProjectFilePath);
        Assert.Null(scan.SignalsByDirectory[""].CsprojContent);
    }

    /// <summary>
    /// A project sitting in its own context keeps answering through the directory listing, and
    /// must not be given a path — that is what selects the image it already builds with.
    /// </summary>
    [Fact]
    public async Task ReadAsync_LeavesTheProjectPathUnset_WhenTheContextHoldsItsOwnProjectFile()
    {
        var gitHub = GitHubWith(
            new Dictionary<string, string[]> { ["server"] = ["Breeze.Api.csproj", "App.sln"] },
            new Dictionary<string, string>
            {
                ["server/Breeze.Api.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />",
                ["server/App.sln"] = Solution
            });

        var scan = await Reader(gitHub).ReadAsync(Token, "acme", "app", "main", ["server"], CancellationToken.None);

        Assert.Null(scan.SignalsByDirectory["server"].ProjectFilePath);
        Assert.Equal("Breeze.Api.csproj", scan.SignalsByDirectory["server"].CsprojFileName);
    }

    [Fact]
    public async Task ReadAsync_ReadsTheRepositoryRootAsAContext()
    {
        var gitHub = GitHubWith(
            new Dictionary<string, string[]> { [""] = ["package.json"] },
            new Dictionary<string, string> { ["package.json"] = """{ "dependencies": { "express": "4" } }""" });

        var scan = await Reader(gitHub).ReadAsync(Token, "acme", "app", "main", [""], CancellationToken.None);

        Assert.Contains("express", scan.SignalsByDirectory[""].PackageJson);
    }

    /// <summary>A directory named by two services is listed once, not once per service.</summary>
    [Fact]
    public async Task ReadAsync_ReadsEachDirectoryOnce()
    {
        var gitHub = GitHubWith(
            new Dictionary<string, string[]> { ["server"] = ["Api.csproj"] },
            new Dictionary<string, string> { ["server/Api.csproj"] = "<Project />" });

        await Reader(gitHub).ReadAsync(Token, "acme", "app", "main", ["server", "server"], CancellationToken.None);

        gitHub.Verify(
            g => g.ListAllContentsAsync(Token, "acme", "app", "server", "main", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A directory holding several projects yields the one that looks like the app, not whichever
    /// GitHub listed first — building YemenHub.Modules instead of YemenHub.Api produces an image
    /// with no entry point, which is the incident RepositoryLayoutResolver was built around.
    /// </summary>
    [Fact]
    public async Task ReadAsync_PrefersAWebProjectWhenADirectoryHoldsSeveral()
    {
        var gitHub = GitHubWith(
            new Dictionary<string, string[]> { ["server"] = ["Nabdah.Modules.csproj", "Nabdah.Api.csproj"] },
            new Dictionary<string, string>
            {
                ["server/Nabdah.Modules.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                ["server/Nabdah.Api.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />"
            });

        var scan = await Reader(gitHub).ReadAsync(Token, "acme", "app", "main", ["server"], CancellationToken.None);

        Assert.Equal("Nabdah.Api.csproj", scan.SignalsByDirectory["server"].CsprojFileName);
    }
}
