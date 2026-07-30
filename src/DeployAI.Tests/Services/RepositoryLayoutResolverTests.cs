using DeployAI.Infrastructure.GitHub;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// One answer to "where does this app live", replacing a guess made independently in seven callers.
/// A wrong guess returned nothing rather than an error, which is indistinguishable from "this repo
/// has no such file" — and in one day on one monorepo that silently cost an env scan, a Dockerfile
/// regeneration, an object-storage provision, and any check on required configuration.
/// See docs/12-repository-scanning.md.
/// </summary>
public class RepositoryLayoutResolverTests
{
    private const string Owner = "tester";
    private const string Repo = "yemenConnect";
    private const string Branch = "main";

    private const string WebCsproj =
        """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""";
    private const string LibraryCsproj =
        """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""";

    [Fact]
    public async Task ResolveAsync_FindsTheAppOneLevelDown_AndPicksTheWebProjectNotTheFirstCsproj()
    {
        // The layout every silent failure happened on: backend/src holds four project directories
        // and no files of its own. Alphabetically YemenHub.Api comes first, so a test that only
        // checked "found something" would pass while the logic was wrong — hence Modules is listed
        // first here deliberately.
        var github = Monorepo();

        var layout = await Resolve(github, "backend/src");

        Assert.Equal("backend/src", layout.BuildRoot);
        Assert.Equal("backend/src/YemenHub.Api", layout.ProjectDirectory);
        Assert.Equal("backend/src/YemenHub.Api/YemenHub.Api.csproj", layout.EntryProjectPath);
        Assert.False(layout.IsInconclusive);
    }

    [Fact]
    public async Task ResolveAsync_SearchesNearestFirst()
    {
        // A file present both beside the app and at the repo root must resolve to the app's copy —
        // that is the one the app actually reads.
        var github = Monorepo();

        var layout = await Resolve(github, "backend/src");

        Assert.Equal(
            ["backend/src/YemenHub.Api", "backend/src", ""],
            layout.SearchPath);
    }

    [Fact]
    public async Task ResolveAsync_StaysPut_WhenTheAppIsDirectlyInTheConfiguredDirectory()
    {
        // The single-project shape, which already worked everywhere and must keep working.
        var github = new Mock<IGitHubService>();
        StubDir(github, "api", File("api/My.Api.csproj"));
        StubFile(github, "api/My.Api.csproj", WebCsproj);

        var layout = await Resolve(github, "api");

        Assert.Equal("api", layout.ProjectDirectory);
        Assert.Equal("api/My.Api.csproj", layout.EntryProjectPath);
    }

    [Fact]
    public async Task ResolveAsync_RefusesToNominateALibrary()
    {
        // A directory of libraries and nothing else. Guessing one is how a build produces an image
        // with no entry point; null is the honest answer.
        var github = new Mock<IGitHubService>();
        StubDir(github, "src", Dir("src/Domain"), Dir("src/Persistence"));
        StubDir(github, "src/Domain", File("src/Domain/Domain.csproj"));
        StubDir(github, "src/Persistence", File("src/Persistence/Persistence.csproj"));
        StubFile(github, "src/Domain/Domain.csproj", LibraryCsproj);
        StubFile(github, "src/Persistence/Persistence.csproj", LibraryCsproj);

        var layout = await Resolve(github, "src");

        Assert.Null(layout.EntryProjectPath);
        // Still usable for reading config: the directories that were looked at stay on the path.
        Assert.Contains("src/Domain", layout.SearchPath);
        Assert.False(layout.IsInconclusive);
    }

    [Fact]
    public async Task ResolveAsync_ReportsBeingBlind_RatherThanReportingAnEmptyRepo()
    {
        // The distinction the whole file exists for. Nothing listed means the scan saw nothing, not
        // that the repository is bare, and a caller must be able to tell.
        var github = new Mock<IGitHubService>();
        github.Setup(g => g.ListAllContentsAsync(
                It.IsAny<string>(), Owner, Repo, It.IsAny<string>(), Branch, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("404"));

        var layout = await Resolve(github, "backend/src");

        Assert.True(layout.IsInconclusive);
        Assert.Empty(layout.DirectoriesRead);
    }

    [Fact]
    public async Task ResolveAsync_RecognisesANodeApp()
    {
        var github = new Mock<IGitHubService>();
        StubDir(github, "apps", Dir("apps/web"));
        StubDir(github, "apps/web", File("apps/web/package.json"));

        var layout = await Resolve(github, "apps");

        Assert.Equal("apps/web", layout.ProjectDirectory);
        Assert.Equal("apps/web/package.json", layout.EntryProjectPath);
    }

    [Fact]
    public async Task FindAsync_ReturnsTheNearestCopyAndSaysWhereItCameFrom()
    {
        // appsettings.json exists beside the app and nowhere else — the case the object-storage scan
        // missed entirely by reading only backend/src.
        var github = Monorepo();
        StubFile(github, "backend/src/YemenHub.Api/appsettings.json", """{"Storage":{"Bucket":"media"}}""");

        var resolver = new RepositoryLayoutResolver(github.Object);
        var layout = await resolver.ResolveAsync("t", Owner, Repo, Branch, "backend/src", CancellationToken.None);

        var found = await resolver.FindAsync("t", Owner, Repo, Branch, layout, "appsettings.json", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("backend/src/YemenHub.Api/appsettings.json", found!.Path);
        Assert.Contains("Storage", found.Content);
    }

    [Fact]
    public async Task FindAllBySuffixAsync_CollectsEveryManifestAlongThePath()
    {
        var github = Monorepo();

        var resolver = new RepositoryLayoutResolver(github.Object);
        var layout = await resolver.ResolveAsync("t", Owner, Repo, Branch, "backend/src", CancellationToken.None);

        var csprojs = await resolver.FindAllBySuffixAsync(
            "t", Owner, Repo, Branch, layout, ".csproj", CancellationToken.None);

        Assert.Contains(csprojs, f => f.Path.EndsWith("YemenHub.Api.csproj"));
    }

    /// <summary>
    /// yemenConnect's real shape. Modules is listed before Api so a resolver that takes the first
    /// csproj it sees fails this.
    /// </summary>
    private static Mock<IGitHubService> Monorepo()
    {
        var github = new Mock<IGitHubService>();
        StubDir(github, "backend/src",
            Dir("backend/src/YemenHub.Modules"),
            Dir("backend/src/YemenHub.Api"),
            Dir("backend/src/YemenHub.Persistence"));

        StubDir(github, "backend/src/YemenHub.Modules", File("backend/src/YemenHub.Modules/YemenHub.Modules.csproj"));
        StubDir(github, "backend/src/YemenHub.Api", File("backend/src/YemenHub.Api/YemenHub.Api.csproj"));
        StubDir(github, "backend/src/YemenHub.Persistence", File("backend/src/YemenHub.Persistence/YemenHub.Persistence.csproj"));

        StubFile(github, "backend/src/YemenHub.Modules/YemenHub.Modules.csproj", LibraryCsproj);
        StubFile(github, "backend/src/YemenHub.Api/YemenHub.Api.csproj", WebCsproj);
        StubFile(github, "backend/src/YemenHub.Persistence/YemenHub.Persistence.csproj", LibraryCsproj);
        return github;
    }

    private static async Task<RepositoryLayout> Resolve(Mock<IGitHubService> github, string? configured) =>
        await new RepositoryLayoutResolver(github.Object)
            .ResolveAsync("t", Owner, Repo, Branch, configured, CancellationToken.None);

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
