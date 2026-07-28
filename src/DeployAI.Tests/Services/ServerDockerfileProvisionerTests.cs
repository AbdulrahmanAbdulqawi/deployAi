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
        var provisioner = new ServerDockerfileProvisioner(github.Object, logger);

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

    private static ServerDockerfileProvisioner Create(Mock<IGitHubService> github) =>
        new(github.Object, NullLogger<ServerDockerfileProvisioner>.Instance);

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
