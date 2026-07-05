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
    public void RankCandidates_ExcludesKnownFrontendDirectories()
    {
        var ranked = ServerProjectDiscoverer.RankCandidates(["client", "src", "web", "My.Api"]);

        Assert.DoesNotContain(ranked, name => string.Equals(name, "client", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ranked, name => string.Equals(name, "web", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ranked, name => string.Equals(name, "My.Api", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExpandNestedCandidates_PrefersApiProjectUnderSrc()
    {
        var nested = ServerProjectDiscoverer.ExpandNestedCandidates(
            "src",
            ["DeployAI.Core", "DeployAI.Api", "DeployAI.Tests"]);

        Assert.Equal("src/DeployAI.Api", nested[0]);
    }

    [Fact]
    public void ShouldScanNestedSubdirectories_ReturnsTrue_ForSrc()
    {
        Assert.True(ServerProjectDiscoverer.ShouldScanNestedSubdirectories("src"));
        Assert.False(ServerProjectDiscoverer.ShouldScanNestedSubdirectories("client"));
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
