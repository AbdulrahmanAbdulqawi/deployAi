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

    // Next.js has a .next output dir but is an SSR app — serving it as static files gives a
    // blank page. It must build with Nixpacks so Coolify runs `next start`.
    [Theory]
    [InlineData("next", ".next")]
    [InlineData("nuxt", ".output")]
    [InlineData("sveltekit", "build")]
    public void ResolveBuildPack_UsesNixpacksForServerRenderedFrontends(string framework, string outputDirectory)
    {
        var request = new CreateProviderProjectRequest(
            "app",
            "owner/repo",
            framework,
            OutputDirectory: outputDirectory);

        Assert.Equal(CoolifyBuildPackValues.Nixpacks, CoolifyApiSupport.ResolveBuildPack(request));
    }

    // Compose is checked before the Dockerfile branch: a compose app's services have their own
    // Dockerfiles, but compose is what Coolify actually deploys.
    [Fact]
    public void ResolveBuildPack_PrefersComposeOverDockerfile()
    {
        var request = new CreateProviderProjectRequest(
            "app",
            "owner/repo",
            "docker",
            DockerfilePath: "server/Dockerfile",
            ComposeFileLocation: "docker-compose.coolify.yml");

        Assert.Equal(CoolifyBuildPackValues.DockerCompose, CoolifyApiSupport.ResolveBuildPack(request));
    }

    [Fact]
    public void ResolveExposedPort_IsNullForCompose()
    {
        var request = new CreateProviderProjectRequest(
            "app", "owner/repo", "dotnet", ComposeFileLocation: "docker-compose.coolify.yml");

        Assert.Null(CoolifyApiSupport.ResolveExposedPort(CoolifyBuildPackValues.DockerCompose, request));
    }

    // A blanket "3000" pointed Coolify's proxy at a port nothing was listening on.
    [Theory]
    [InlineData("dotnet", "8080")]
    [InlineData("aspnetcore", "8080")]
    [InlineData("python", "8000")]
    [InlineData("node", "3000")]
    [InlineData(null, "3000")]
    public void ResolveExposedPort_MatchesTheFrameworkDefault(string? framework, string expected)
    {
        var request = new CreateProviderProjectRequest("app", "owner/repo", framework);

        Assert.Equal(expected, CoolifyApiSupport.ResolveExposedPort(CoolifyBuildPackValues.Nixpacks, request));
    }

    [Fact]
    public void ResolveExposedPort_IsEightyForStaticSites()
    {
        var request = new CreateProviderProjectRequest("app", "owner/repo", null, OutputDirectory: "dist");

        Assert.Equal("80", CoolifyApiSupport.ResolveExposedPort(CoolifyBuildPackValues.Static, request));
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
        Assert.Equal("8080", CoolifyApiSupport.ResolveExposedPort(
            CoolifyBuildPackValues.Dockerfile, exposedPort: null, framework: null));
        Assert.Equal("80", CoolifyApiSupport.ResolveExposedPort(
            CoolifyBuildPackValues.Static, exposedPort: null, framework: null));
        Assert.Equal("3000", CoolifyApiSupport.ResolveExposedPort(
            CoolifyBuildPackValues.Nixpacks, exposedPort: null, framework: null));
    }

    [Fact]
    public void NormalizeGitHubRepoUrl_BuildsHttpsUrl()
    {
        Assert.Equal(
            "https://github.com/acme/widget",
            CoolifyApiSupport.NormalizeGitHubRepoUrl("acme/widget"));
    }
}
