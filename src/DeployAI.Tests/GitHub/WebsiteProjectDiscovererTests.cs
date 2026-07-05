using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.GitHub;

public class WebsiteProjectDiscovererTests
{
    [Fact]
    public void RankCandidates_PrefersClientDirectory()
    {
        var ranked = WebsiteProjectDiscoverer.RankCandidates(["src", "client", "docs"]);

        Assert.Equal("client", ranked[0]);
    }

    [Fact]
    public void HasWebsiteSignals_ReturnsTrue_WhenAngularJsonPresent()
    {
        var contents = new[]
        {
            new GitHubContentItem("angular.json", "client/angular.json", "file")
        };

        Assert.True(WebsiteProjectDiscoverer.HasWebsiteSignals(contents));
    }
}
