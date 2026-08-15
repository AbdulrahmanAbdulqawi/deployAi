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
        Assert.True(profile.DockerUsesRepositoryRoot);
    }

    [Fact]
    public void Detect_UsesServiceDirectory_ForNestedSolutionDockerfile()
    {
        const string dockerfile = """
            COPY DeployAI.Api/DeployAI.Api.csproj DeployAI.Api/
            COPY DeployAI.Providers/DeployAI.Providers.csproj DeployAI.Providers/
            COPY . .
            """;

        var profile = _detector.Detect("src", true, dockerfile, null, null);

        Assert.Equal("src", profile.RootDirectory);
        Assert.Null(profile.DockerfilePath);
        Assert.Equal("src", profile.ServiceDirectory);
        Assert.Equal("docker", profile.Framework);
        Assert.False(profile.DockerUsesRepositoryRoot);
    }

    [Fact]
    public void Detect_ResolvesServiceDirectoryFromTheEntrypoint_ForARepositoryRootMonorepoBuild()
    {
        // Mirqab's actual shape: root-context multi-stage build, several sibling .csproj files
        // COPY'd for restore caching, and one published. RootDirectory correctly answers "the
        // repository root" -- that is where `docker build` has to run -- but ServiceDirectory used
        // to answer the same question and was therefore always "", so a caller going looking for
        // appsettings.json afterward searched the repository root, found nothing, and reported no
        // database requirement for an app that fails on line one of Program.cs without one.
        const string dockerfile = """
            FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS api
            COPY src/Mirqab.Core/Mirqab.Core.csproj src/Mirqab.Core/
            COPY src/Mirqab.Data/Mirqab.Data.csproj src/Mirqab.Data/
            COPY src/Mirqab.Application/Mirqab.Application.csproj src/Mirqab.Application/
            COPY src/Mirqab.Api/Mirqab.Api.csproj src/Mirqab.Api/
            RUN dotnet restore src/Mirqab.Api/Mirqab.Api.csproj
            COPY src/ src/
            RUN dotnet publish src/Mirqab.Api/Mirqab.Api.csproj -c Release -o /app

            FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
            COPY --from=api /app ./
            ENTRYPOINT ["dotnet", "Mirqab.Api.dll"]
            """;

        var profile = _detector.Detect(string.Empty, true, dockerfile, null, null);

        // The build layout is unaffected -- Docker still has to build from the repository root.
        Assert.Equal(string.Empty, profile.RootDirectory);
        Assert.True(profile.DockerUsesRepositoryRoot);
        // What was wrong: the app's own source is at src/Mirqab.Api, not the repository root.
        Assert.Equal("src/Mirqab.Api", profile.ServiceDirectory);
    }

    [Fact]
    public void Detect_FallsBackToTheBuildRoot_WhenNoEntrypointCanBeMatched()
    {
        // Not every Dockerfile publishes with `dotnet X.dll`, and guessing which sibling .csproj is
        // the right one without that anchor is exactly the wrong move -- an unmatched entry answers
        // "unknown", not "probably this one".
        const string dockerfile = """
            COPY iDaara.Server/iDaara.Server.csproj iDaara.Server/
            COPY idaara.client/idaara.client.esproj idaara.client/
            """;

        var profile = _detector.Detect("iDaara.Server", true, dockerfile, null, null);

        Assert.Equal("iDaara.Server", profile.ServiceDirectory);
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
