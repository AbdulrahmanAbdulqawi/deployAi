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
        Assert.Equal("dotnet out/DeployAI.Api.dll", profile.StartCommand);
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
    public void Detect_UsesPython_WhenRequirementsTxtPresent()
    {
        const string requirements = "fastapi\nuvicorn";

        var profile = _detector.Detect("api", false, null, null, null, requirements);

        Assert.Equal("python", profile.Framework);
        Assert.Equal("pip install -r requirements.txt", profile.InstallCommand);
        Assert.Contains("uvicorn", profile.StartCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_UsesGo_WhenGoModPresent()
    {
        const string goMod = "module example.com/app\ngo 1.22";

        var profile = _detector.Detect("api", false, null, null, null, null, null, goMod);

        Assert.Equal("go", profile.Framework);
        Assert.Equal("go build -o app .", profile.BuildCommand);
        Assert.Equal("./app", profile.StartCommand);
    }

    [Fact]
    public void Detect_UsesRust_WhenCargoTomlPresent()
    {
        const string cargo = """
            [package]
            name = "deployai"
            version = "0.1.0"
            """;

        var profile = _detector.Detect("api", false, null, null, null, null, null, null, cargo);

        Assert.Equal("rust", profile.Framework);
        Assert.Equal("cargo build --release", profile.BuildCommand);
        Assert.Equal("./target/release/deployai", profile.StartCommand);
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
