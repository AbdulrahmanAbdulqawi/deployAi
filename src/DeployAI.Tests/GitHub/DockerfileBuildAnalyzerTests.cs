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
}
