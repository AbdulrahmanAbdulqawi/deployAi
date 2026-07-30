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
        Assert.Contains("exec dotnet YemenHub.Api.dll", dockerfile);
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
        Assert.Contains("exec dotnet My.Api.dll", dockerfile);
    }

    [Fact]
    public void Build_UnknownTargetFramework_DefaultsToNet8()
    {
        var dockerfile = DotnetServerDockerfile.Build("api", "api", csprojContent: "<Project></Project>", "My.Api.csproj");

        Assert.Contains("dotnet/sdk:8.0", dockerfile);
    }

    [Fact]
    public void Build_BundlesMigrationsAndRunsThemBeforeTheApp()
    {
        // A provisioned database starts empty and nothing else creates its schema. yemenconnect-api
        // deployed against one and failed every query with 42P01: relation "events" does not exist,
        // while reporting healthy -- it was listening, so only the responses gave it away.
        var dockerfile = DotnetServerDockerfile.Build(
            buildRootDirectory: "backend/src",
            serviceDirectory: "backend/src/YemenHub.Api",
            csprojContent: "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
            csprojFileName: "YemenHub.Api.csproj");

        Assert.Contains("dotnet ef migrations bundle", dockerfile);
        Assert.Contains($"--output /app/{DotnetServerDockerfile.MigrationBundleName}", dockerfile);

        // Bundled in the SDK stage, because the runtime image has no SDK to build one.
        var bundleAt = dockerfile.IndexOf("dotnet ef migrations bundle", StringComparison.Ordinal);
        var runtimeStageAt = dockerfile.IndexOf("FROM mcr.microsoft.com/dotnet/aspnet", StringComparison.Ordinal);
        Assert.True(bundleAt < runtimeStageAt, "the bundle must be produced before the runtime stage");
    }

    [Fact]
    public void Build_DoesNotFailTheBuildWhenThereAreNoMigrations()
    {
        // Plenty of services have no EF at all. Bundling is best effort: nothing to bundle is a
        // normal outcome, not a broken image.
        var dockerfile = DotnetServerDockerfile.Build(
            buildRootDirectory: "api",
            serviceDirectory: "api",
            csprojContent: "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
            csprojFileName: "My.Api.csproj");

        Assert.Contains("|| echo", dockerfile);
        Assert.Contains($"if [ -x ./{DotnetServerDockerfile.MigrationBundleName} ]", dockerfile);
    }

    [Fact]
    public void Build_StopsTheContainerWhenMigrationsFail()
    {
        // The alternative is an app that boots against a half-built schema and 500s on every
        // request while looking perfectly healthy -- which is precisely how this went unnoticed.
        var dockerfile = DotnetServerDockerfile.Build(
            buildRootDirectory: "api",
            serviceDirectory: "api",
            csprojContent: "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
            csprojFileName: "My.Api.csproj");

        Assert.Contains($"./{DotnetServerDockerfile.MigrationBundleName} || exit 1", dockerfile);

        // And the app is only reached after that gate.
        var gateAt = dockerfile.IndexOf("|| exit 1", StringComparison.Ordinal);
        var appAt = dockerfile.IndexOf("exec dotnet My.Api.dll", StringComparison.Ordinal);
        Assert.True(gateAt < appAt, "the app must start only after migrations have succeeded");
    }

    [Fact]
    public void Build_InstallsTheKerberosLibraryNpgsqlNeeds()
    {
        // Npgsql loads GSSAPI when opening a connection and the ASP.NET runtime image does not ship
        // it, so every Postgres query failed with "libgssapi_krb5.so.2: cannot open shared object
        // file" -- from an app that had started cleanly. The failure is per-request, so nothing
        // about the container's state gives it away.
        var dockerfile = DotnetServerDockerfile.Build(
            buildRootDirectory: "backend/src",
            serviceDirectory: "backend/src/YemenHub.Api",
            csprojContent: "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
            csprojFileName: "YemenHub.Api.csproj");

        Assert.Contains("libgssapi-krb5-2", dockerfile);

        // In the runtime stage: the build stage's image is discarded, so installing it there does
        // nothing for the app that actually runs.
        var runtimeStageAt = dockerfile.IndexOf("FROM mcr.microsoft.com/dotnet/aspnet", StringComparison.Ordinal);
        var installAt = dockerfile.IndexOf("libgssapi-krb5-2", StringComparison.Ordinal);
        Assert.True(installAt > runtimeStageAt, "the library must be installed into the runtime image");
    }
}
