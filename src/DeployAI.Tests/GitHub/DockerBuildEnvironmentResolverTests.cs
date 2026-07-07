using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.GitHub;

public class DockerBuildEnvironmentResolverTests
{
    private const string IDaaraDockerfile = """
        FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
        WORKDIR /src
        COPY [iDaara.Server/iDaara.Server.csproj, iDaara.Server/]
        COPY [idaara.client/idaara.client.esproj, idaara.client/]
        COPY . .
        WORKDIR /src/iDaara.Server
        RUN dotnet build "./iDaara.Server.csproj" -c Release -o /app/build
        """;

    [Fact]
    public void Resolve_UsesRepositoryRoot_ForCrossFolderMonorepoDockerfile()
    {
        var entries = DockerBuildEnvironmentResolver.Resolve(IDaaraDockerfile, "iDaara.Server");

        Assert.Equal(".", entries["rootDirectory"]);
        Assert.Equal("true", entries["dockerUsesRepositoryRoot"]);
        Assert.Equal("iDaara.Server/Dockerfile", entries["dockerfilePath"]);
        Assert.Equal("iDaara.Server", entries["serviceDirectory"]);
    }

    [Fact]
    public void Resolve_UsesServiceDirectory_ForNestedSolutionDockerfile()
    {
        const string dockerfile = """
            COPY DeployAI.Api/DeployAI.Api.csproj DeployAI.Api/
            COPY DeployAI.Providers/DeployAI.Providers.csproj DeployAI.Providers/
            COPY . .
            """;

        var entries = DockerBuildEnvironmentResolver.Resolve(dockerfile, "src");

        Assert.Equal("src", entries["rootDirectory"]);
        Assert.Equal("false", entries["dockerUsesRepositoryRoot"]);
        Assert.Equal("Dockerfile", entries["dockerfilePath"]);
        Assert.Equal("src", entries["serviceDirectory"]);
    }
}
