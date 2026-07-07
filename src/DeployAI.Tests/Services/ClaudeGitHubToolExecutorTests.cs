using DeployAI.Api.Services;
using DeployAI.Infrastructure.GitHub;
using Moq;
using System.Text.Json;

namespace DeployAI.Tests.Services;

public class ClaudeGitHubToolExecutorTests
{
    [Fact]
    public async Task ReadFile_ReturnsContentForValidPath()
    {
        var gitHub = new Mock<IGitHubService>();
        gitHub
            .Setup(s => s.GetFileContentAsync("token", "owner", "repo", "src/app/app.component.ts", "abc123", It.IsAny<CancellationToken>()))
            .ReturnsAsync("export class AppComponent {}");

        var executor = new ClaudeGitHubToolExecutor(gitHub.Object);
        var input = JsonDocument.Parse("""{"path":"src/app/app.component.ts"}""").RootElement;

        var result = await executor.ExecuteAsync(
            "github_read_file",
            input,
            new GitHubRepoRef("token", "owner", "repo", "abc123"),
            CancellationToken.None);

        Assert.Contains("export class AppComponent", result);
    }

    [Fact]
    public async Task ReadFile_RejectsPathTraversal()
    {
        var gitHub = new Mock<IGitHubService>();
        var executor = new ClaudeGitHubToolExecutor(gitHub.Object);
        var input = JsonDocument.Parse("""{"path":"../secrets.env"}""").RootElement;

        var result = await executor.ExecuteAsync(
            "github_read_file",
            input,
            new GitHubRepoRef("token", "owner", "repo", "abc123"),
            CancellationToken.None);

        Assert.Contains("path traversal", result, StringComparison.OrdinalIgnoreCase);
        gitHub.Verify(
            s => s.GetFileContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ListDirectory_ReturnsEntries()
    {
        var gitHub = new Mock<IGitHubService>();
        gitHub
            .Setup(s => s.ListContentsAsync("token", "owner", "repo", "client", "abc123", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new GitHubContentItem("src", "client/src", "dir"),
                new GitHubContentItem("package.json", "client/package.json", "file")
            ]);

        var executor = new ClaudeGitHubToolExecutor(gitHub.Object);
        var input = JsonDocument.Parse("""{"path":"client"}""").RootElement;

        var result = await executor.ExecuteAsync(
            "github_list_directory",
            input,
            new GitHubRepoRef("token", "owner", "repo", "abc123"),
            CancellationToken.None);

        Assert.Contains("[dir] client/src", result);
        Assert.Contains("[file] client/package.json", result);
    }
}
