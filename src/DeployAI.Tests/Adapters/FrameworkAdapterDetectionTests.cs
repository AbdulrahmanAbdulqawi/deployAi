using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Infrastructure.Adapters;

namespace DeployAI.Tests.Adapters;

public class FrameworkAdapterDetectionTests
{
    private static readonly IFrameworkAdapter[] AllAdapters =
    [
        new AngularAdapter(),
        new DotnetAdapter(),
        new NodeExpressAdapter()
    ];

    private const string AngularPackageJson = """
        { "dependencies": { "@angular/core": "^19.0.0" }, "scripts": { "build": "ng build", "start": "ng serve" } }
        """;

    private const string ExpressPackageJson = """
        { "main": "server.js", "dependencies": { "express": "^4.19.0" }, "scripts": { "start": "node server.js" } }
        """;

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
        """;

    // The ranking that matters: an Angular package.json must be claimed by Angular, not by the
    // generic Node adapter that would also match its start script.
    [Fact]
    public void AngularPackageJson_IsWonByAngular_NotNode()
    {
        var signals = new RepositorySignals("client", PackageJson: AngularPackageJson);

        var claims = AllAdapters
            .Select(a => (a.Id, Detection: a.Detect(signals)))
            .Where(c => c.Detection is not null)
            .OrderByDescending(c => c.Detection!.Confidence)
            .ToList();

        Assert.Equal("angular", claims[0].Id);
    }

    [Fact]
    public void ExpressPackageJson_IsWonByNode()
    {
        var signals = new RepositorySignals("server", PackageJson: ExpressPackageJson);

        var angular = new AngularAdapter().Detect(signals);
        var node = new NodeExpressAdapter().Detect(signals);

        Assert.Null(angular);
        Assert.NotNull(node);
        Assert.True(node!.Capabilities.HasCapability(ServiceCapability.HttpService));
    }

    [Fact]
    public void Csproj_IsWonByDotnet()
    {
        var signals = new RepositorySignals(
            "server/YemeniBreeze.Api",
            CsprojContent: Csproj,
            CsprojFileName: "YemeniBreeze.Api.csproj");

        var detection = new DotnetAdapter().Detect(signals);

        Assert.NotNull(detection);
        Assert.True(detection!.Capabilities.HasCapability(ServiceCapability.HttpService));
    }

    // A frontend-framework package.json must not be claimed as a Node backend even when it has
    // a start script — that path produced exactly the kind of mis-generation adapters prevent.
    [Theory]
    [InlineData("""{ "dependencies": { "next": "14.0.0" }, "scripts": { "start": "next start" } }""")]
    [InlineData("""{ "dependencies": { "@angular/core": "19.0.0" }, "scripts": { "start": "ng serve" } }""")]
    public void FrontendPackageJson_IsNeverClaimedByNodeBackend(string packageJson)
    {
        var detection = new NodeExpressAdapter().Detect(new RepositorySignals("app", PackageJson: packageJson));

        Assert.Null(detection);
    }

    [Fact]
    public void EmptyDirectory_IsClaimedByNoOne()
    {
        var signals = new RepositorySignals("docs");

        Assert.All(AllAdapters, adapter => Assert.Null(adapter.Detect(signals)));
    }
}

public class FrameworkAdapterNodeCreationTests
{
    [Fact]
    public void DotnetAdapter_GeneratesDockerfile_WithAssemblyNameAndSdkFromCsproj()
    {
        var signals = new RepositorySignals(
            "server/YemeniBreeze.Api",
            CsprojContent: """
                <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """,
            CsprojFileName: "YemeniBreeze.Api.csproj");

        var node = new DotnetAdapter().CreateServiceNode("api", signals);

        var dockerfile = node.Source.Dockerfile!.Content!;
        Assert.Contains("sdk:10.0", dockerfile);
        Assert.Contains("aspnet:10.0", dockerfile);
        Assert.Contains("\"YemeniBreeze.Api.dll\"", dockerfile);
        Assert.Contains("8080", dockerfile);
        Assert.Equal("/api/health", node.HealthPath);
    }

    [Fact]
    public void DotnetAdapter_RespectsARepoProvidedDockerfile()
    {
        var signals = new RepositorySignals(
            "server/Api",
            CsprojContent: "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
            HasDockerfile: true,
            DockerfileContent: "FROM custom");

        var node = new DotnetAdapter().CreateServiceNode("api", signals);

        Assert.Null(node.Source.Dockerfile!.Content);
        Assert.Equal("Dockerfile", node.Source.Dockerfile.ExistingPath);
    }

    // The proof of pluggability: same interface, different image/entrypoint/port/health.
    [Fact]
    public void NodeAdapter_GeneratesANodeContainer_NotADotnetOne()
    {
        var signals = new RepositorySignals(
            "server",
            PackageJson: """{ "main": "src/app.js", "dependencies": { "express": "4.0.0" } }""");

        var node = new NodeExpressAdapter().CreateServiceNode("api", signals);

        var dockerfile = node.Source.Dockerfile!.Content!;
        Assert.Contains("node:22-alpine", dockerfile);
        Assert.Contains("\"src/app.js\"", dockerfile);
        Assert.DoesNotContain("dotnet", dockerfile);
        Assert.Equal([3000], node.ListenPorts);
        Assert.Equal("/health", node.HealthPath);
    }

    [Fact]
    public void NodeAdapter_FindsTheEntryPoint_FromTheStartScript_WhenMainIsAbsent()
    {
        Assert.Equal("server.js", NodeExpressAdapter.ResolveEntryPoint(
            """{ "scripts": { "start": "node server.js" } }"""));
        Assert.Equal("index.js", NodeExpressAdapter.ResolveEntryPoint("{}"));
    }

    [Fact]
    public void AngularAdapter_GeneratesAnNginxServedBundle()
    {
        var signals = new RepositorySignals(
            "client",
            PackageJson: """{ "dependencies": { "@angular/core": "19.0.0" }, "scripts": { "build": "ng build" } }""",
            AngularJson: """
                { "projects": { "client": { "architect": { "build": { "options": { "outputPath": "dist/client" } } } } } }
                """);

        var node = new AngularAdapter().CreateServiceNode("web", signals);

        var dockerfile = node.Source.Dockerfile!.Content!;
        Assert.Contains("nginx:alpine", dockerfile);
        Assert.Contains("nginx.conf", dockerfile);
        Assert.True(node.Capabilities.HasCapability(ServiceCapability.StaticSite));
        Assert.Equal([80], node.ListenPorts);
    }
}
