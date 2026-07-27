using DeployAI.Core.Providers;
using DeployAI.Providers.Coolify;

namespace DeployAI.Tests.Providers;

public class CoolifyBuildPackTests
{
    [Theory]
    [InlineData("nixpacks", CoolifyBuildPack.Nixpacks)]
    [InlineData("static", CoolifyBuildPack.Static)]
    [InlineData("dockerfile", CoolifyBuildPack.Dockerfile)]
    public void TryParse_ParsesKnownValues(string value, CoolifyBuildPack expected)
    {
        Assert.True(CoolifyBuildPackValues.TryParse(value, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(value, CoolifyBuildPackValues.ToApiValue(parsed));
    }

    [Fact]
    public void ResolveBuildPack_UsesDockerfileWhenFrameworkIsDocker()
    {
        var request = new CreateProviderProjectRequest("app", "owner/repo", "docker");
        Assert.Equal(CoolifyBuildPackValues.Dockerfile, CoolifyApiSupport.ResolveBuildPack(request));
    }

    [Fact]
    public void ResolveBuildPack_UsesStaticWhenOutputDirectoryProvidedWithNoBuildCommand()
    {
        var request = new CreateProviderProjectRequest(
            "app",
            "owner/repo",
            null,
            OutputDirectory: "dist");
        Assert.Equal(CoolifyBuildPackValues.Static, CoolifyApiSupport.ResolveBuildPack(request));
    }

    [Fact]
    public void ResolveBuildPack_UsesNixpacksWhenOutputDirectoryHasBuildCommand()
    {
        // "static" skips the build step entirely - an app with a build command (Angular,
        // React, etc.) needs nixpacks to actually compile before serving OutputDirectory.
        var request = new CreateProviderProjectRequest(
            "app",
            "owner/repo",
            "angular",
            OutputDirectory: "dist/app/browser",
            BuildCommand: "npm run build");
        Assert.Equal(CoolifyBuildPackValues.Nixpacks, CoolifyApiSupport.ResolveBuildPack(request));
    }

    [Fact]
    public void ResolveExposedPort_UsesDotNetDefaultForDockerfileBuilds()
    {
        Assert.Equal("8080", CoolifyApiSupport.ResolveExposedPort(CoolifyBuildPackValues.Dockerfile));
        Assert.Equal("80", CoolifyApiSupport.ResolveExposedPort(CoolifyBuildPackValues.Static));
        Assert.Equal("3000", CoolifyApiSupport.ResolveExposedPort(CoolifyBuildPackValues.Nixpacks));
    }

    [Fact]
    public void NormalizeGitHubRepoUrl_BuildsHttpsUrl()
    {
        Assert.Equal(
            "https://github.com/acme/widget",
            CoolifyApiSupport.NormalizeGitHubRepoUrl("acme/widget"));
    }
}
