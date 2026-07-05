using System.Net;
using System.Net.Http.Json;
using DeployAI.Infrastructure.GitHub;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Infrastructure;

public class GitHubServiceTests
{
    private static GitHubService CreateService(MockHttpMessageHandler handler)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DeployAI.Infrastructure.Options.GitHubOptions
        {
            ClientId = "id",
            ClientSecret = "secret",
            CallbackPath = "/callback"
        });
        var appOptions = Microsoft.Extensions.Options.Options.Create(new DeployAI.Infrastructure.Options.AppOptions
        {
            ApiUrl = "http://localhost:5000"
        });
        return new GitHubService(handler.ToHttpClient(), options, appOptions);
    }

    [Fact]
    public async Task ListContentsAsync_ReturnsDirectoriesOnly()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.github.com/repos/owner/repo/contents?ref=main")
            .Respond(HttpStatusCode.OK, "application/json", """
            [
              { "name": "client", "path": "client", "type": "dir" },
              { "name": "README.md", "path": "README.md", "type": "file" },
              { "name": "src", "path": "src", "type": "dir" }
            ]
            """);

        var service = CreateService(handler);
        var items = await service.ListContentsAsync("token", "owner", "repo", null, "main", CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal("client", items[0].Name);
        Assert.Equal("src", items[1].Name);
    }

    [Fact]
    public async Task ListContentsAsync_NormalizesNestedPath()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.github.com/repos/owner/repo/contents/client?ref=main")
            .Respond(HttpStatusCode.OK, "application/json", """
            [
              { "name": "src", "path": "client/src", "type": "dir" }
            ]
            """);

        var service = CreateService(handler);
        var items = await service.ListContentsAsync("token", "owner", "repo", "/client/", "main", CancellationToken.None);

        Assert.Single(items);
        Assert.Equal("client/src", items[0].Path);
    }
}
