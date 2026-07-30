using DeployAI.Infrastructure.GitHub;
using Moq;

namespace DeployAI.Tests.Graph;

/// <summary>
/// The env scan reading only the repository root, plus one supplied server path, is what put an API
/// into production crash-looping on "Jwt configuration missing": appsettings.json was never at
/// backend/src, so the scan found nothing, the wizard showed no environment step, and the app
/// deployed with nothing set. The same blindness hid Tickets:SigningKey until a request 500'd.
/// <para>
/// These assert on the layout the scan now reads through, since that is where the fix lives — the
/// detector itself is a pure function over content it is handed.
/// </para>
/// </summary>
public class EnvScanLayoutTests
{
    private const string Owner = "tester";
    private const string Repo = "yemenConnect";
    private const string Branch = "main";

    private const string Appsettings = """
        {
          "Jwt": { "SigningKey": "" },
          "Tickets": { "SigningKey": "" },
          "Cors": { "Origins": [] }
        }
        """;

    [Fact]
    public async Task TheScanReachesConfigurationOneLevelBelowTheServerPath()
    {
        // serverPath is backend/src; the configuration is at backend/src/YemenHub.Api. Reading only
        // "backend/src/appsettings.json" — what the endpoint did before — finds nothing at all.
        var github = Monorepo();
        var resolver = new RepositoryLayoutResolver(github.Object);

        var layout = await resolver.ResolveAsync("t", Owner, Repo, Branch, "backend/src", CancellationToken.None);
        var found = await resolver.FindAsync("t", Owner, Repo, Branch, layout, "appsettings.json", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("backend/src/YemenHub.Api/appsettings.json", found!.Path);

        // The keys whose absence caused two production incidents.
        Assert.Contains("Jwt", found.Content);
        Assert.Contains("Tickets", found.Content);
    }

    [Fact]
    public async Task DetectingNothingFromANestedRepoIsNowRecognisableAsAWrongAnswer()
    {
        // Before, "no variables" and "I never found the file" were the same response. The layout now
        // carries where it looked, so the difference is visible.
        var github = Monorepo();
        var resolver = new RepositoryLayoutResolver(github.Object);

        var layout = await resolver.ResolveAsync("t", Owner, Repo, Branch, "backend/src", CancellationToken.None);

        Assert.False(layout.IsInconclusive);
        Assert.Equal("backend/src/YemenHub.Api", layout.ProjectDirectory);
        Assert.Contains("backend/src/YemenHub.Api", layout.SearchPath);
    }

    [Fact]
    public async Task AnUnreadableRepositoryIsReportedRatherThanScannedAsEmpty()
    {
        // The response marks itself inconclusive on this, which is what stops a deploy proceeding as
        // though the app declared no configuration.
        var github = new Mock<IGitHubService>();
        github.Setup(g => g.ListAllContentsAsync(
                It.IsAny<string>(), Owner, Repo, It.IsAny<string>(), Branch, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("403"));

        var layout = await new RepositoryLayoutResolver(github.Object)
            .ResolveAsync("t", Owner, Repo, Branch, "backend/src", CancellationToken.None);

        Assert.True(layout.IsInconclusive);
    }

    [Fact]
    public async Task RootLevelFilesAreStillFound_ForARepoThatKeepsThemThere()
    {
        // Nearest-first must not mean root-blind: most repos keep compose and the README at the top,
        // and the search path ends there.
        var github = Monorepo();
        StubFile(github, "docker-compose.yml", "services:\n  db:\n    image: postgres:16\n");

        var resolver = new RepositoryLayoutResolver(github.Object);
        var layout = await resolver.ResolveAsync("t", Owner, Repo, Branch, "backend/src", CancellationToken.None);
        var compose = await resolver.FindAsync("t", Owner, Repo, Branch, layout, "docker-compose.yml", CancellationToken.None);

        Assert.NotNull(compose);
        Assert.Equal("docker-compose.yml", compose!.Path);
    }

    private static Mock<IGitHubService> Monorepo()
    {
        var github = new Mock<IGitHubService>();
        StubDir(github, "backend/src",
            Dir("backend/src/YemenHub.Modules"),
            Dir("backend/src/YemenHub.Api"));
        StubDir(github, "backend/src/YemenHub.Modules", File("backend/src/YemenHub.Modules/YemenHub.Modules.csproj"));
        StubDir(github, "backend/src/YemenHub.Api",
            File("backend/src/YemenHub.Api/YemenHub.Api.csproj"),
            File("backend/src/YemenHub.Api/appsettings.json"));

        StubFile(github, "backend/src/YemenHub.Modules/YemenHub.Modules.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        StubFile(github, "backend/src/YemenHub.Api/YemenHub.Api.csproj",
            """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        StubFile(github, "backend/src/YemenHub.Api/appsettings.json", Appsettings);
        return github;
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
