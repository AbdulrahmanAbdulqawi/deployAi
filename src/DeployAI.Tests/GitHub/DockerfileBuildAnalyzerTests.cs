using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.GitHub;

public class DockerfileBuildAnalyzerTests
{
    private const string MonorepoDockerfile = """
        FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
        WORKDIR /src
        COPY [iDaara.Server/iDaara.Server.csproj, iDaara.Server/]
        COPY [idaara.client/idaara.client.esproj, idaara.client/]
        COPY . .
        WORKDIR /src/iDaara.Server
        RUN dotnet build "./iDaara.Server.csproj" -c Release -o /app/build
        """;

    [Fact]
    public void RequiresRepositoryRoot_ReturnsTrue_ForMonorepoDockerfile()
    {
        Assert.True(DockerfileBuildAnalyzer.RequiresRepositoryRoot(MonorepoDockerfile, "iDaara.Server"));
    }

    [Fact]
    public void RequiresRepositoryRoot_ReturnsFalse_ForLocalDockerfile()
    {
        const string dockerfile = """
            FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
            WORKDIR /src
            COPY ["MyApi.csproj", "."]
            RUN dotnet restore
            COPY . .
            """;

        Assert.False(DockerfileBuildAnalyzer.RequiresRepositoryRoot(dockerfile, "MyApi"));
    }

    [Fact]
    public void BuildDockerfilePath_UsesServiceDirectory()
    {
        Assert.Equal("iDaara.Server/Dockerfile", DockerfileBuildAnalyzer.BuildDockerfilePath("iDaara.Server"));
    }

    [Fact]
    public void ResolveDockerBuildLayout_UsesRepositoryRoot_ForCrossFolderMonorepoDockerfile()
    {
        var layout = DockerfileBuildAnalyzer.ResolveDockerBuildLayout(MonorepoDockerfile, "iDaara.Server");

        Assert.Equal(".", layout.RootDirectory);
        Assert.Equal("iDaara.Server/Dockerfile", layout.DockerfilePath);
    }

    [Fact]
    public void ResolveDockerBuildLayout_UsesServiceDirectory_ForNestedSolutionDockerfile()
    {
        const string dockerfile = """
            FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
            WORKDIR /app
            COPY DeployAI.Api/DeployAI.Api.csproj DeployAI.Api/
            COPY DeployAI.Providers/DeployAI.Providers.csproj DeployAI.Providers/
            COPY . .
            RUN dotnet publish DeployAI.Api/DeployAI.Api.csproj -c Release -o out
            """;

        var layout = DockerfileBuildAnalyzer.ResolveDockerBuildLayout(dockerfile, "src");

        Assert.Equal("src", layout.RootDirectory);
        Assert.Equal("Dockerfile", layout.DockerfilePath);
    }
}
