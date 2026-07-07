using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Deployments;

public class DeployTargetConfigTests
{
    [Fact]
    public void Parse_ReturnsEmptyConfig_WhenJsonIsEmpty()
    {
        var config = DeployTargetConfig.Parse("{}");
        Assert.Null(config.RootDirectory);
        Assert.Empty(config.ToEnvironmentEntries());
    }

    [Fact]
    public void Parse_DeserializesRootDirectory()
    {
        var config = DeployTargetConfig.Parse("""{"rootDirectory":"client","role":"website"}""");
        Assert.Equal("client", config.RootDirectory);
        Assert.Equal("website", config.Role);
    }

    [Fact]
    public void ToEnvironmentEntries_TrimsSlashes()
    {
        var config = DeployTargetConfig.Parse("""{"rootDirectory":"/client/"}""");
        var entries = config.ToEnvironmentEntries();
        Assert.Equal("client", entries["rootDirectory"]);
    }

    [Fact]
    public void FromProfile_MapsAllBuildFields()
    {
        var profile = new FrontendBuildProfile("idaara.client", "npm run build", "npm install", "dist/idaara.client", "angular");
        var config = DeployTargetConfig.FromProfile(profile, "website");

        Assert.Equal("idaara.client", config.RootDirectory);
        Assert.Equal("website", config.Role);
        Assert.Equal("dist/idaara.client", config.OutputDirectory);
        Assert.Equal("npm run build", config.BuildCommand);
        Assert.Equal("angular", config.Framework);

        var entries = config.ToEnvironmentEntries();
        Assert.Equal("dist/idaara.client", entries["outputDirectory"]);
        Assert.Equal("angular", entries["framework"]);
    }

    [Fact]
    public void FromServerProfile_MapsStartCommand()
    {
        var profile = new ServerBuildProfile("src/api", "dotnet build", null, "dotnet run", "dotnet");
        var config = DeployTargetConfig.FromServerProfile(profile, "server");

        Assert.Equal("src/api", config.RootDirectory);
        Assert.Equal("dotnet run", config.StartCommand);
        Assert.Equal("dotnet", config.Framework);

        var entries = config.ToEnvironmentEntries();
        Assert.Equal("dotnet run", entries["startCommand"]);
    }

    [Fact]
    public void ToEnvironmentEntries_PreservesDockerRootDirectory()
    {
        var config = DeployTargetConfig.FromServerProfile(
            new ServerBuildProfile("src", null, null, null, "docker", null, "src"),
            "server");

        var entries = config.ToEnvironmentEntries();

        Assert.Equal("src", entries["rootDirectory"]);
        Assert.False(entries.ContainsKey("dockerfilePath"));
    }

    [Fact]
    public void ToEnvironmentEntries_UsesRepositoryRoot_ForCrossFolderDockerfile()
    {
        var config = DeployTargetConfig.FromServerProfile(
            new ServerBuildProfile(string.Empty, null, null, null, "docker", "iDaara.Server/Dockerfile", "iDaara.Server", true),
            "server");

        var entries = config.ToEnvironmentEntries();

        Assert.Equal(".", entries["rootDirectory"]);
        Assert.Equal("iDaara.Server/Dockerfile", entries["dockerfilePath"]);
        Assert.Equal("true", entries["dockerUsesRepositoryRoot"]);
    }
}
