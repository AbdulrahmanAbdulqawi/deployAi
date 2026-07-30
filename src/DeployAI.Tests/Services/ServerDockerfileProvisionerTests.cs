using System.Runtime.CompilerServices;
using DeployAI.Api.Services;
using DeployAI.Infrastructure.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

public class ServerDockerfileProvisionerTests
{
    private const string Owner = "tester";
    private const string Repo = "yemenConnect";
    private const string Branch = "main";

    [Fact]
    public async Task EnsureDockerfileAsync_FindsTheEntryProjectOneLevelDown()
    {
        // yemenConnect's shape, and the reason this method exists: backend/src holds four project
        // directories and no csproj of its own. ListAllContentsAsync reads one level despite its
        // name, so the search found nothing, returned null, and the Dockerfile was silently never
        // regenerated -- a fix to the generator could not reach the app it was written for.
        var github = new Mock<IGitHubService>();
        StubDirectory(github, "backend/src",
            Dir("backend/src/YemenHub.Api"),
            Dir("backend/src/YemenHub.Modules"),
            Dir("backend/src/YemenHub.Persistence"));

        StubDirectory(github, "backend/src/YemenHub.Api", File("backend/src/YemenHub.Api/YemenHub.Api.csproj"));
        StubDirectory(github, "backend/src/YemenHub.Modules", File("backend/src/YemenHub.Modules/YemenHub.Modules.csproj"));
        StubDirectory(github, "backend/src/YemenHub.Persistence", File("backend/src/YemenHub.Persistence/YemenHub.Persistence.csproj"));

        StubFile(github, "backend/src/YemenHub.Api/YemenHub.Api.csproj",
            """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        StubFile(github, "backend/src/YemenHub.Modules/YemenHub.Modules.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        StubFile(github, "backend/src/YemenHub.Persistence/YemenHub.Persistence.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");

        string? written = null;
        github.Setup(g => g.UpsertFileAsync(
                It.IsAny<string>(), Owner, Repo, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), Branch, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string _, string content,
                       string _, string _, string? _, CancellationToken _) => written = content)
            .ReturnsAsync("sha");

        var result = await Create(github).EnsureDockerfileAsync(
            "token", Owner, Repo, Branch, "backend/src", "backend/src", CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(written);

        // The web project, not the first csproj it happened to see -- building YemenHub.Modules
        // would produce an image with no entry point.
        Assert.Contains("YemenHub.Api/YemenHub.Api.csproj", written);
        Assert.DoesNotContain("YemenHub.Modules.csproj", written);
    }

    [Fact]
    public async Task EnsureDockerfileAsync_ReturnsNullAndSaysSo_WhenThereIsNoEntryProject()
    {
        // A server that is not .NET at all. Returning null is correct -- but it used to be silent,
        // which is indistinguishable from "never ran" when you are reading logs to find out why a
        // generated file did not change.
        var github = new Mock<IGitHubService>();
        StubDirectory(github, "services/api", Dir("services/api/src"));
        StubDirectory(github, "services/api/src", File("services/api/src/index.ts"));

        var logger = new CountingLogger<ServerDockerfileProvisioner>();
        var provisioner = new ServerDockerfileProvisioner(
            github.Object, new RepositoryLayoutResolver(github.Object), new FrontendBuildDetector(), logger);

        var result = await provisioner.EnsureDockerfileAsync(
            "token", Owner, Repo, Branch, "services/api", "services/api", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, logger.Warnings);

        github.Verify(g => g.UpsertFileAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureDockerfileAsync_StillPrefersACsprojSittingDirectlyInTheServiceDirectory()
    {
        // The single-project shape, which already worked and must keep working: no descent needed.
        var github = new Mock<IGitHubService>();
        StubDirectory(github, "api", File("api/My.Api.csproj"));
        StubFile(github, "api/My.Api.csproj",
            """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");

        string? written = null;
        github.Setup(g => g.UpsertFileAsync(
                It.IsAny<string>(), Owner, Repo, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), Branch, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string _, string content,
                       string _, string _, string? _, CancellationToken _) => written = content)
            .ReturnsAsync("sha");

        var result = await Create(github).EnsureDockerfileAsync(
            "token", Owner, Repo, Branch, "api", "api", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("My.Api.csproj", written);
    }

    [Fact]
    public async Task EnsureDockerfileAsync_WritesNothingWhenTheFileIsAlreadyWhatItWouldGenerate()
    {
        // Four consecutive deploys of one app appended four commits to the user's own history that
        // changed nothing, all sharing one message. Generation is deterministic, so a redeploy of an
        // unchanged app must produce no commit at all -- a repository DeployAI writes into belongs
        // to someone, and its history is a product surface.
        var github = SingleProject();
        var generated = await GenerateOnceAsync(github);

        // Second run: the file is now exactly what the generator produces.
        var second = new Mock<IGitHubService>();
        StubDirectory(second, "api", File("api/My.Api.csproj"));
        StubFile(second, "api/My.Api.csproj", WebCsproj);
        second.Setup(g => g.GetFileMetadataAsync(
                It.IsAny<string>(), Owner, Repo, "api/Dockerfile", Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubFileMetadata(generated, "sha-1"));

        var result = await Create(second).EnsureDockerfileAsync(
            "token", Owner, Repo, Branch, "api", "api", CancellationToken.None);

        // Still reports where the build context is -- doing nothing is not the same as failing.
        Assert.NotNull(result);
        Assert.Equal("api", result.BaseDirectory);

        second.Verify(g => g.UpsertFileAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureDockerfileAsync_CommitsWhenTheGeneratedContentHasChanged()
    {
        // The other half: a real change must still reach the repository, and must not claim to be
        // adding a file that is already there.
        var github = SingleProject();
        github.Setup(g => g.GetFileMetadataAsync(
                It.IsAny<string>(), Owner, Repo, "api/Dockerfile", Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubFileMetadata("FROM scratch\n# an older generation\n", "sha-1"));

        string? message = null;
        github.Setup(g => g.UpsertFileAsync(
                It.IsAny<string>(), Owner, Repo, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), Branch, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string _, string _,
                       string commitMessage, string _, string? _, CancellationToken _) => message = commitMessage)
            .ReturnsAsync("sha-2");

        await Create(github).EnsureDockerfileAsync(
            "token", Owner, Repo, Branch, "api", "api", CancellationToken.None);

        Assert.NotNull(message);
        Assert.StartsWith("Update Dockerfile", message);
    }

    [Fact]
    public async Task EnsureDockerfileAsync_ComparesAgainstDecodedContent()
    {
        // The reason the guard never fired: GetFileMetadataAsync already decodes, and the comparison
        // decoded it a second time, threw FormatException on plain text, swallowed it, and answered
        // "different". A caught exception returning the unsafe answer is invisible.
        var github = SingleProject();
        var generated = await GenerateOnceAsync(github);

        Assert.DoesNotContain("RnJvbSA", generated, StringComparison.Ordinal);
        Assert.Contains("FROM ", generated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureSsrWebsiteDockerfileAsync_WritesNothingWhenTheFileIsUnchanged()
    {
        var first = new Mock<IGitHubService>();
        StubDirectory(first, "apps/web", File("apps/web/package.json"));
        StubFile(first, "apps/web/package.json",
            """{ "name": "web", "dependencies": { "next": "15.0.0" }, "scripts": { "build": "next build" } }""");
        var written = CaptureUpsert(first, out _);

        await Create(first).EnsureSsrWebsiteDockerfileAsync(
            "token", Owner, Repo, Branch, "apps/web", "next", [], null, null, null, "dist", CancellationToken.None);
        Assert.NotNull(written.Value);

        var second = new Mock<IGitHubService>();
        StubDirectory(second, "apps/web", File("apps/web/package.json"));
        StubFile(second, "apps/web/package.json",
            """{ "name": "web", "dependencies": { "next": "15.0.0" }, "scripts": { "build": "next build" } }""");
        second.Setup(g => g.GetFileMetadataAsync(
                It.IsAny<string>(), Owner, Repo, "apps/web/Dockerfile", Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubFileMetadata(written.Value!, "sha-1"));

        var result = await Create(second).EnsureSsrWebsiteDockerfileAsync(
            "token", Owner, Repo, Branch, "apps/web", "next", [], null, null, null, "dist", CancellationToken.None);

        Assert.NotNull(result);
        second.Verify(g => g.UpsertFileAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private const string WebCsproj =
        """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""";

    private static Mock<IGitHubService> SingleProject()
    {
        var github = new Mock<IGitHubService>();
        StubDirectory(github, "api", File("api/My.Api.csproj"));
        StubFile(github, "api/My.Api.csproj", WebCsproj);
        return github;
    }

    /// <summary>Runs the generator once against an empty repository and returns what it wrote.</summary>
    private static async Task<string> GenerateOnceAsync(Mock<IGitHubService> github)
    {
        var written = CaptureUpsert(github, out _);
        await Create(github).EnsureDockerfileAsync(
            "token", Owner, Repo, Branch, "api", "api", CancellationToken.None);
        Assert.NotNull(written.Value);
        return written.Value!;
    }

    [Fact]
    public async Task EnsureSsrWebsiteDockerfileAsync_FindsTheFrontendOneLevelDown()
    {
        // The website half of the same gap. This read only the configured directory, so a target
        // pointed at "apps" with the Next.js app in "apps/web" found no package.json, returned null,
        // and the site stayed on Nixpacks -- which bakes the source's localhost API URL into the
        // bundle. The generated Dockerfile exists to stop exactly that, and could not reach it.
        var github = new Mock<IGitHubService>();
        StubDirectory(github, "apps", Dir("apps/web"));
        StubDirectory(github, "apps/web", File("apps/web/package.json"));
        StubFile(github, "apps/web/package.json",
            """{ "name": "web", "dependencies": { "next": "15.0.0" }, "scripts": { "build": "next build" } }""");

        var written = CaptureUpsert(github, out var path);

        var result = await Create(github).EnsureSsrWebsiteDockerfileAsync(
            "token", Owner, Repo, Branch, "apps", "next", ["NEXT_PUBLIC_API_URL"],
            null, null, null, "dist", CancellationToken.None);

        Assert.NotNull(result);

        // The build context has to be the directory the package.json is in, or the image installs
        // from a directory with no manifest.
        Assert.Equal("apps/web", result.BaseDirectory);
        Assert.Equal("apps/web/Dockerfile", path.Value);
        Assert.Contains("NEXT_PUBLIC_API_URL", written.Value);
    }

    [Fact]
    public async Task EnsureSsrWebsiteDockerfileAsync_RefusesToBuildADifferentAppFoundNearby()
    {
        // Descending without evidence is worse than not descending. A website target pointed at a
        // directory holding both halves would otherwise take whichever manifest was listed first --
        // here the API's -- and build the backend as the website.
        var github = new Mock<IGitHubService>();
        StubDirectory(github, "", Dir("server"), Dir("client"));
        StubDirectory(github, "server", File("server/package.json"));
        StubFile(github, "server/package.json",
            """{ "name": "api", "dependencies": { "express": "4.19.0" } }""");

        var result = await Create(github).EnsureSsrWebsiteDockerfileAsync(
            "token", Owner, Repo, Branch, string.Empty, "next", [], null, null, null, "dist", CancellationToken.None);

        Assert.Null(result);
        github.Verify(g => g.UpsertFileAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureSsrWebsiteDockerfileAsync_TrustsTheDirectoryTheUserChose()
    {
        // No evidence check when it stays put: the user pointed at this directory, and a framework
        // the detector does not recognise is not a reason to override them.
        var github = new Mock<IGitHubService>();
        StubDirectory(github, "client", File("client/package.json"));
        StubFile(github, "client/package.json",
            """{ "name": "site", "scripts": { "build": "custom-tool build" } }""");

        var written = CaptureUpsert(github, out _);

        var result = await Create(github).EnsureSsrWebsiteDockerfileAsync(
            "token", Owner, Repo, Branch, "client", "next", [], null, null, null, "dist", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("client", result.BaseDirectory);
        Assert.NotNull(written.Value);
    }

    [Fact]
    public async Task EnsureSsrWebsiteDockerfileAsync_SaysItCouldNotRead_RatherThanNothingToDo()
    {
        // Leaving a site on Nixpacks because the repository could not be read is a different fact
        // from leaving it there because the app is not Node, and they used to log identically.
        var github = new Mock<IGitHubService>();
        var logger = new CountingLogger<ServerDockerfileProvisioner>();
        var provisioner = new ServerDockerfileProvisioner(
            github.Object, new RepositoryLayoutResolver(github.Object), new FrontendBuildDetector(), logger);

        var result = await provisioner.EnsureSsrWebsiteDockerfileAsync(
            "token", Owner, Repo, Branch, "apps/web", "next", [], null, null, null, "dist", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, logger.Warnings);
    }

    /// <summary>Captures the content and path of the single file the provisioner writes.</summary>
    private static StrongBox<string?> CaptureUpsert(Mock<IGitHubService> github, out StrongBox<string?> path)
    {
        var content = new StrongBox<string?>(null);
        var capturedPath = new StrongBox<string?>(null);
        path = capturedPath;

        github.Setup(g => g.UpsertFileAsync(
                It.IsAny<string>(), Owner, Repo, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), Branch, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string filePath, string fileContent,
                       string _, string _, string? _, CancellationToken _) =>
            {
                capturedPath.Value = filePath;
                content.Value = fileContent;
            })
            .ReturnsAsync("sha");

        return content;
    }

    /// <summary>The real resolver over the same mocked GitHub service: these tests exist to prove the
    /// monorepo descent picks the web project, so stubbing it would remove what they check.</summary>
    private static ServerDockerfileProvisioner Create(Mock<IGitHubService> github) =>
        new(github.Object,
            new RepositoryLayoutResolver(github.Object),
            new FrontendBuildDetector(),
            NullLogger<ServerDockerfileProvisioner>.Instance);

    private static GitHubContentItem Dir(string path) =>
        new(System.IO.Path.GetFileName(path), path, "dir");

    private static GitHubContentItem File(string path) =>
        new(System.IO.Path.GetFileName(path), path, "file");

    private static void StubDirectory(Mock<IGitHubService> github, string path, params GitHubContentItem[] items) =>
        github.Setup(g => g.ListAllContentsAsync(
                It.IsAny<string>(), Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

    private static void StubFile(Mock<IGitHubService> github, string path, string content) =>
        github.Setup(g => g.GetFileContentAsync(
                It.IsAny<string>(), Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);

    /// <summary>Counts warnings so a test can assert the failure was reported, not just that it happened.</summary>
    private sealed class CountingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
