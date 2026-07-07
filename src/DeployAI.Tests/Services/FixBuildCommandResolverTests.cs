using DeployAI.Api.Services;
using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

public class FixBuildCommandResolverTests
{
    [Fact]
    public void Resolve_VercelAngular_UsesRootDirectoryAndNpmCommands()
    {
        var config = DeployTargetConfig.Parse("""{"role":"website","rootDirectory":"client","framework":"angular"}""");

        var plan = FixBuildCommandResolver.Resolve("vercel", config, "angular", [], null);

        Assert.Equal("client", plan.WorkingDirectory);
        Assert.Equal("npm install", plan.InstallCommand);
        Assert.Equal("npm run build", plan.BuildCommand);
    }

    [Fact]
    public void Resolve_RailwayDotNet_UsesCsprojFromReferencedFiles()
    {
        var config = DeployTargetConfig.Parse("""{"role":"server","serviceDirectory":"TicketHub.Server","framework":"AspNetCore"}""");
        var files = new[] { "TicketHub.Server/Program.cs" };

        var plan = FixBuildCommandResolver.Resolve("railway", config, "AspNetCore", files, null);

        Assert.Equal("TicketHub.Server", plan.WorkingDirectory);
        Assert.Null(plan.InstallCommand);
        Assert.Equal("dotnet build \"TicketHub.Server.csproj\"", plan.BuildCommand);
    }

    [Fact]
    public void Resolve_Dockerfile_UsesDotnetProjectRelativeToServiceDirectory()
    {
        var config = DeployTargetConfig.Parse("""{"role":"server","serviceDirectory":"TicketHub.Server","dockerfilePath":"TicketHub.Server/Dockerfile"}""");
        const string dockerfile = """
            FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
            RUN dotnet build "TicketHub.Server/TicketHub.Server.csproj" -c Release
            """;

        var plan = FixBuildCommandResolver.Resolve("railway", config, "AspNetCore", [], dockerfile);

        Assert.Equal("dotnet build \"TicketHub.Server.csproj\"", plan.BuildCommand);
    }

    [Fact]
    public void Resolve_Dockerfile_StripsDotSlashFromProjectPath()
    {
        var config = DeployTargetConfig.Parse("""{"role":"server","serviceDirectory":"TicketHub.Server"}""");
        const string dockerfile = """
            RUN dotnet publish "./TicketHub.Server.csproj" -c Release
            """;

        var plan = FixBuildCommandResolver.Resolve("railway", config, "AspNetCore", [], dockerfile);

        Assert.Equal("dotnet build \"TicketHub.Server.csproj\"", plan.BuildCommand);
    }

    [Theory]
    [InlineData("TicketHub.Server/TicketHub.Server.csproj", "TicketHub.Server", "TicketHub.Server.csproj")]
    [InlineData("./TicketHub.Server.csproj", "TicketHub.Server", "TicketHub.Server.csproj")]
    [InlineData("src/TicketHub.Server/TicketHub.Server.csproj", ".", "src/TicketHub.Server/TicketHub.Server.csproj")]
    public void NormalizeCsprojForWorkingDirectory_MakesPathRelativeToWorkingDir(
        string csprojPath,
        string workingDirectory,
        string expected)
    {
        var normalized = FixBuildCommandResolver.NormalizeCsprojForWorkingDirectory(csprojPath, workingDirectory);

        Assert.Equal(expected, normalized);
    }
}
