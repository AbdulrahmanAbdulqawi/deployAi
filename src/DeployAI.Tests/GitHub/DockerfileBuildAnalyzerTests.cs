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

    [Fact]
    public void ResolveEntryProjectDirectory_MatchesTheCsprojNamedByEntrypoint()
    {
        const string dockerfile = """
            COPY src/Mirqab.Core/Mirqab.Core.csproj src/Mirqab.Core/
            COPY src/Mirqab.Api/Mirqab.Api.csproj src/Mirqab.Api/
            ENTRYPOINT ["dotnet", "Mirqab.Api.dll"]
            """;

        Assert.Equal("src/Mirqab.Api", DockerfileBuildAnalyzer.ResolveEntryProjectDirectory(dockerfile));
    }

    [Fact]
    public void ResolveEntryProjectDirectory_MatchesTheShellFormEntrypointToo()
    {
        const string dockerfile = """
            COPY src/Mirqab.Api/Mirqab.Api.csproj src/Mirqab.Api/
            ENTRYPOINT dotnet Mirqab.Api.dll
            """;

        Assert.Equal("src/Mirqab.Api", DockerfileBuildAnalyzer.ResolveEntryProjectDirectory(dockerfile));
    }

    [Fact]
    public void ResolveEntryProjectDirectory_ReturnsNull_WhenNoCsprojNameMatchesTheEntrypoint()
    {
        // An unmatched entry means "unknown", not "guess the nearest one" -- a wrong guess here
        // sends every later config lookup to the wrong directory with no sign anything is off.
        const string dockerfile = """
            COPY src/Other.Api/Other.Api.csproj src/Other.Api/
            ENTRYPOINT ["dotnet", "Mirqab.Api.dll"]
            """;

        Assert.Null(DockerfileBuildAnalyzer.ResolveEntryProjectDirectory(dockerfile));
    }

    [Fact]
    public void ResolveEntryProjectDirectory_ReturnsNull_WithNoEntrypointAtAll()
    {
        const string dockerfile = "COPY src/Mirqab.Api/Mirqab.Api.csproj src/Mirqab.Api/";

        Assert.Null(DockerfileBuildAnalyzer.ResolveEntryProjectDirectory(dockerfile));
    }
}
