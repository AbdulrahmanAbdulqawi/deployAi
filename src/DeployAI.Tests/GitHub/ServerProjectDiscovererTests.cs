using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.GitHub;

public class ServerProjectDiscovererTests
{
    [Fact]
    public void RankCandidates_PrefersDotServerDirectory()
    {
        var ranked = ServerProjectDiscoverer.RankCandidates(
        [
            "docs",
            "iDaara.Application",
            "iDaara.Server",
            "idaara.client"
        ]);

        Assert.Equal("iDaara.Server", ranked[0]);
    }

    [Fact]
    public void HasServerSignals_ReturnsTrue_WhenCsprojPresent()
    {
        var contents = new[]
        {
            new GitHubContentItem("iDaara.Server.csproj", "iDaara.Server/iDaara.Server.csproj", "file")
        };

        Assert.True(ServerProjectDiscoverer.HasServerSignals(contents));
    }

    [Theory]
    [InlineData("iDaara.Server", 100)]
    [InlineData("My.Api", 90)]
    [InlineData("server", 80)]
    [InlineData("docs", 0)]
    public void ScoreDirectoryName_RanksKnownServerFolders(string name, int minimumScore)
    {
        Assert.True(ServerProjectDiscoverer.ScoreDirectoryName(name) >= minimumScore);
    }
}
