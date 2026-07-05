using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.GitHub;

public class ServerBuildDetectorTests
{
    private readonly ServerBuildDetector _detector = new();

    [Fact]
    public void Detect_UsesDocker_WhenDockerfilePresent()
    {
        var profile = _detector.Detect("src/api", true, null, null, null);

        Assert.Equal("src/api", profile.RootDirectory);
        Assert.Equal("docker", profile.Framework);
        Assert.Null(profile.DockerfilePath);
    }

    [Fact]
    public void Detect_UsesRepositoryRoot_ForMonorepoDockerfile()
    {
        const string dockerfile = """
            COPY [iDaara.Server/iDaara.Server.csproj, iDaara.Server/]
            COPY [idaara.client/idaara.client.esproj, idaara.client/]
            """;

        var profile = _detector.Detect("iDaara.Server", true, dockerfile, null, null);

        Assert.Equal(string.Empty, profile.RootDirectory);
        Assert.Equal("iDaara.Server/Dockerfile", profile.DockerfilePath);
        Assert.Equal("iDaara.Server", profile.ServiceDirectory);
        Assert.Equal("docker", profile.Framework);
    }

    [Fact]
    public void Detect_UsesDotnet_WhenCsprojPresent()
    {
        var profile = _detector.Detect("src/api", false, null, null, "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        Assert.Equal("dotnet", profile.Framework);
        Assert.Equal("src/api", profile.RootDirectory);
        Assert.Equal("src/api", profile.ServiceDirectory);
        Assert.Null(profile.BuildCommand);
        Assert.Null(profile.StartCommand);
    }

    [Fact]
    public void Detect_UsesBuildRoot_ForDotnetMonorepoWithProjectReferences()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <ProjectReference Include="..\DeployAI.Core\DeployAI.Core.csproj" />
              </ItemGroup>
            </Project>
            """;

        var profile = _detector.Detect("src/DeployAI.Api", false, null, null, csproj);

        Assert.Equal("dotnet", profile.Framework);
        Assert.Equal("src", profile.RootDirectory);
        Assert.Equal("src/DeployAI.Api", profile.ServiceDirectory);
        Assert.Equal("dotnet publish DeployAI.Api/DeployAI.Api.csproj -c Release -o out", profile.BuildCommand);
    }

    [Fact]
    public void Detect_UsesNodeScripts_WhenPackageJsonPresent()
    {
        const string packageJson = """
            {
              "scripts": {
                "build": "tsc",
                "start": "node dist/index.js"
              }
            }
            """;

        var profile = _detector.Detect("server", false, null, packageJson, null);

        Assert.Equal("node", profile.Framework);
        Assert.Equal("npm run build", profile.BuildCommand);
        Assert.Equal("npm start", profile.StartCommand);
        Assert.Equal("npm install", profile.InstallCommand);
    }

    [Fact]
    public void Detect_ReturnsNoFramework_ForAngularPackageJson()
    {
        const string packageJson = """
            {
              "dependencies": {
                "@angular/core": "18.0.0"
              },
              "scripts": {
                "start": "ng serve",
                "build": "ng build"
              }
            }
            """;

        var profile = _detector.Detect("client", false, null, packageJson, null);

        Assert.Null(profile.Framework);
        Assert.Null(profile.BuildCommand);
        Assert.Null(profile.StartCommand);
    }
}
