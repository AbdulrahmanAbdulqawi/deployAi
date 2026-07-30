using DeployAI.Infrastructure.GitHub;
using Moq;

namespace DeployAI.Tests.GitHub;

/// <summary>
/// Two questions live in this class, and only one of them belongs to the shared resolver.
/// "Which of these sibling directories is the server" is ranking; "where inside the directory the
/// user named" is layout resolution. Mixing them is how a repository root ends up nominating the
/// Angular client as the backend.
/// </summary>
public class ServerBuildProfileDiscoveryTests
{
    private const string Owner = "owner";
    private const string Repo = "repo";
    private const string Branch = "main";

    private const string WebCsproj = """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""";
    private const string LibraryCsproj = """<Project Sdk="Microsoft.NET.Sdk"></Project>""";

    [Fact]
    public async Task DiscoverAsync_FindsTheAppInsideTheDirectoryTheUserNamed()
    {
        // The wizard's folder picker calls this with whatever the user chose. Given "backend/src"
        // it read that one directory, found no manifest, and returned a profile with no framework
        // and no commands -- so the screen pre-filled nothing and looked like DeployAI did not
        // recognise the repository, when it had simply refused to look one directory further.
        var github = new Mock<IGitHubService>();
        StubDir(github, "backend/src",
            Dir("backend/src/YemenHub.Modules"),
            Dir("backend/src/YemenHub.Api"));
        StubDir(github, "backend/src/YemenHub.Modules", File("backend/src/YemenHub.Modules/YemenHub.Modules.csproj"));
        StubDir(github, "backend/src/YemenHub.Api", File("backend/src/YemenHub.Api/YemenHub.Api.csproj"));
        StubFile(github, "backend/src/YemenHub.Modules/YemenHub.Modules.csproj", LibraryCsproj);
        StubFile(github, "backend/src/YemenHub.Api/YemenHub.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <ProjectReference Include="..\YemenHub.Modules\YemenHub.Modules.csproj" />
              </ItemGroup>
            </Project>
            """);

        var profile = await Discover(github, "backend/src");

        Assert.Equal("dotnet", profile.Framework);
        Assert.Equal("backend/src/YemenHub.Api", profile.ServiceDirectory);

        // A modular monolith builds from the parent, since the entry project references its
        // siblings -- and the publish path names the web project, not the library that sorts first.
        Assert.Equal("backend/src", profile.RootDirectory);
        Assert.Equal("dotnet publish YemenHub.Api/YemenHub.Api.csproj -c Release -o out", profile.BuildCommand);
    }

    [Fact]
    public async Task DiscoverAsync_StaysPutWhenTheNamedDirectoryIsTheApp()
    {
        var github = new Mock<IGitHubService>();
        StubDir(github, "api", File("api/My.Api.csproj"));
        StubFile(github, "api/My.Api.csproj", WebCsproj);

        var profile = await Discover(github, "api");

        Assert.Equal("dotnet", profile.Framework);
        Assert.Equal("api", profile.ServiceDirectory);
    }

    [Fact]
    public async Task DiscoverAsync_ReportsNoFrameworkWhenThereIsNoAppBelowEither()
    {
        var github = new Mock<IGitHubService>();
        StubDir(github, "docs", Dir("docs/guides"));
        StubDir(github, "docs/guides", File("docs/guides/index.md"));

        var profile = await Discover(github, "docs");

        Assert.Null(profile.Framework);
    }

    [Fact]
    public async Task DiscoverAsync_AtTheRepositoryRoot_PicksTheBackendAndNotTheFrontend()
    {
        // Why the whole-repository case keeps its own ranking. The resolver takes the first
        // directory holding any application manifest, and "client" sorts before "server" -- so
        // resolving here would classify the Angular app as the backend and deploy it as an API.
        var github = new Mock<IGitHubService>();
        StubDir(github, string.Empty, Dir("client"), Dir("server"));
        StubDir(github, "client", File("client/package.json"));
        StubDir(github, "server", File("server/My.Api.csproj"));
        StubFile(github, "client/package.json", """{ "dependencies": { "@angular/core": "19.0.0" } }""");
        StubFile(github, "server/My.Api.csproj", WebCsproj);

        var profile = await Discover(github, string.Empty);

        Assert.Equal("dotnet", profile.Framework);
        Assert.Equal("server", profile.ServiceDirectory);

        // And the resolver, asked the same thing, answers with the client — which is correct for
        // the question it was designed for and wrong for this one.
        var layout = await new RepositoryLayoutResolver(github.Object)
            .ResolveAsync("token", Owner, Repo, Branch, string.Empty, CancellationToken.None);
        Assert.Equal("client", layout.ProjectDirectory);
    }

    private static async Task<DeployAI.Core.Deployments.ServerBuildProfile> Discover(
        Mock<IGitHubService> github,
        string path)
    {
        var discovery = new ServerBuildProfileDiscovery(
            github.Object,
            new ServerBuildDetector(),
            new RepositoryLayoutResolver(github.Object));

        return await discovery.DiscoverAsync("token", Owner, Repo, path, Branch, CancellationToken.None);
    }

    private static GitHubContentItem Dir(string path) => new(Path.GetFileName(path), path, "dir");

    private static GitHubContentItem File(string path) => new(Path.GetFileName(path), path, "file");

    private static void StubDir(Mock<IGitHubService> github, string path, params GitHubContentItem[] items) =>
        github.Setup(g => g.ListAllContentsAsync(
                It.IsAny<string>(), Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

    private static void StubFile(Mock<IGitHubService> github, string path, string content) =>
        github.Setup(g => g.GetFileContentAsync(
                It.IsAny<string>(), Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
}
