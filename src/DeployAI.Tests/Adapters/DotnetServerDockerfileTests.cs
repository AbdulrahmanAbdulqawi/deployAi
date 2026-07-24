using DeployAI.Infrastructure.Adapters;

namespace DeployAI.Tests.Adapters;

public class DotnetServerDockerfileTests
{
    [Fact]
    public void Build_ModularMonolith_PublishesEntryProjectFromBuildRoot()
    {
        // yemenConnect's shape: the API csproj is nested under backend/src and references sibling
        // projects, so the context is backend/src, COPY . . brings them all, and publish targets
        // the API by its path relative to the root — the shape nixpacks (single-project) can't do.
        var dockerfile = DotnetServerDockerfile.Build(
            buildRootDirectory: "backend/src",
            serviceDirectory: "backend/src/YemenHub.Api",
            csprojContent: "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            csprojFileName: "YemenHub.Api.csproj");

        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build", dockerfile);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:10.0", dockerfile);
        Assert.Contains("COPY . .", dockerfile);
        Assert.Contains("RUN dotnet publish YemenHub.Api/YemenHub.Api.csproj -c Release -o /app", dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"YemenHub.Api.dll\"]", dockerfile);
        Assert.Contains("EXPOSE 8080", dockerfile);
    }

    [Fact]
    public void Build_SingleProjectApi_PublishesCsprojDirectly()
    {
        var dockerfile = DotnetServerDockerfile.Build(
            buildRootDirectory: "api",
            serviceDirectory: "api",
            csprojContent: "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
            csprojFileName: "My.Api.csproj");

        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build", dockerfile);
        Assert.Contains("RUN dotnet publish My.Api.csproj -c Release -o /app", dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"My.Api.dll\"]", dockerfile);
    }

    [Fact]
    public void Build_UnknownTargetFramework_DefaultsToNet8()
    {
        var dockerfile = DotnetServerDockerfile.Build("api", "api", csprojContent: "<Project></Project>", "My.Api.csproj");

        Assert.Contains("dotnet/sdk:8.0", dockerfile);
    }
}
