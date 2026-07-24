using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Generation;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Core.Deployments.Graph.Transforms;
using DeployAI.Infrastructure.Adapters;

namespace DeployAI.Tests.Graph;

public class ComposeGeneratorTests
{
    private const string AngularPackageJson = """
        { "dependencies": { "@angular/core": "19.0.0" }, "scripts": { "build": "npm run build" } }
        """;

    private const string AngularJson = """
        { "projects": { "client": { "architect": { "build": { "options": { "outputPath": "dist/client" } } } } } }
        """;

    private static DeploymentGraph BuildGraph(IFrameworkAdapter backendAdapter, RepositorySignals backendSignals)
    {
        var web = new AngularAdapter().CreateServiceNode(
            "web",
            new RepositorySignals("client", PackageJson: AngularPackageJson, AngularJson: AngularJson));
        var api = backendAdapter.CreateServiceNode("api", backendSignals);

        var graph = new DeploymentGraph([web, api], [], []);
        return SingleOriginTransform.Apply(graph, new SingleOriginTransform.Options(
            Domain: "breeze.example.com", MaxBodySize: "210m", WebSockets: true));
    }

    private static DeploymentGraph DotnetGraph() => BuildGraph(
        new DotnetAdapter(),
        new RepositorySignals(
            "server/YemeniBreeze.Api",
            CsprojContent: "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
            CsprojFileName: "YemeniBreeze.Api.csproj"));

    private static DeploymentGraph NodeGraph() => BuildGraph(
        new NodeExpressAdapter(),
        new RepositorySignals(
            "server",
            PackageJson: """{ "main": "server.js", "dependencies": { "express": "4.0.0" } }"""));

    [Fact]
    public void Generate_EmitsComposeDockerfilesAndNginx()
    {
        var artifacts = new ComposeGenerator().Generate(DotnetGraph());

        Assert.NotNull(artifacts.Find("docker-compose.coolify.yml"));
        Assert.NotNull(artifacts.Find("client/Dockerfile"));
        Assert.NotNull(artifacts.Find("server/YemeniBreeze.Api/Dockerfile"));
        Assert.NotNull(artifacts.Find("client/nginx.conf"));
    }

    [Fact]
    public void Compose_ExposesOnlyTheIngressService_AndNeverHostPorts()
    {
        var compose = new ComposeGenerator().Generate(DotnetGraph()).Find("docker-compose.coolify.yml")!.Content;

        Assert.Contains("build: ./client", compose);
        Assert.Contains("build: ./server/YemeniBreeze.Api", compose);
        Assert.Contains("expose:", compose);
        // Host ports collide with the Traefik that fronts every Coolify deployment. Matched as
        // an indented YAML key so the header comment mentioning "ports" can't false-positive.
        Assert.DoesNotContain("\n    ports:", compose.Replace("\r\n", "\n"));
        Assert.Contains("depends_on:", compose);
        Assert.Contains("restart: unless-stopped", compose);
    }

    // The proxy contract comes from the edge, not a template: buffering, body size, and
    // websockets all flow from ProxyRouteEdge into the rendered nginx.conf.
    [Fact]
    public void Nginx_RendersTheRouteContract_FromTheEdge()
    {
        var nginx = new ComposeGenerator().Generate(DotnetGraph()).Find("client/nginx.conf")!.Content;

        Assert.Contains("proxy_pass http://api:8080", nginx);
        Assert.Contains("client_max_body_size 210m", nginx);
        Assert.Contains("proxy_request_buffering off", nginx);
        Assert.Contains("Upgrade $http_upgrade", nginx);
        Assert.Contains("try_files $uri $uri/ /index.html", nginx);
    }

    // The pluggability proof: swap the backend adapter and only the backend artifacts change.
    // The shape, the compose skeleton, and the nginx route survive; the port follows the node.
    [Fact]
    public void SwappingTheBackendAdapter_ChangesOnlyBackendArtifacts()
    {
        var dotnet = new ComposeGenerator().Generate(DotnetGraph());
        var node = new ComposeGenerator().Generate(NodeGraph());

        // Same shape either way.
        Assert.NotNull(dotnet.Find("client/nginx.conf"));
        Assert.NotNull(node.Find("client/nginx.conf"));
        Assert.Contains("proxy_pass http://api:8080", dotnet.Find("client/nginx.conf")!.Content);
        Assert.Contains("proxy_pass http://api:3000", node.Find("client/nginx.conf")!.Content);

        var dotnetDockerfile = dotnet.Find("server/YemeniBreeze.Api/Dockerfile")!.Content;
        var nodeDockerfile = node.Find("server/Dockerfile")!.Content;
        Assert.Contains("dotnet publish", dotnetDockerfile);
        Assert.Contains("node:22-alpine", nodeDockerfile);
        Assert.DoesNotContain("dotnet", nodeDockerfile);

        // The frontend is untouched by the backend swap.
        Assert.Equal(dotnet.Find("client/Dockerfile")!.Content, node.Find("client/Dockerfile")!.Content);
    }

    [Fact]
    public void Compose_RendersEnvRequirements_AsPlatformInterpolation()
    {
        var graph = DotnetGraph();
        var api = graph.FindService("api")!;
        graph = graph.WithUpdatedService(api with
        {
            EnvRequirements = [new DetectedEnvVar("JWT_KEY", IsSecret: true)]
        });

        var compose = new ComposeGenerator().Generate(graph).Find("docker-compose.coolify.yml")!.Content;

        Assert.Contains("- JWT_KEY=${JWT_KEY}", compose);
    }

    [Fact]
    public void Compose_RendersDatabaseNodes_WithImageAndNamedVolume()
    {
        var graph = DotnetGraph()
            .WithService(new ServiceNode(
                "db",
                ServiceCapability.Database,
                ServiceSource.FromImage("postgres:16"),
                Volumes: [new VolumeMount("dbdata", "/var/lib/postgresql/data")]));

        var compose = new ComposeGenerator().Generate(graph).Find("docker-compose.coolify.yml")!.Content;

        Assert.Contains("image: postgres:16", compose);
        Assert.Contains("dbdata:/var/lib/postgresql/data", compose);
        Assert.Contains("volumes:\n  dbdata:", compose.Replace("\r\n", "\n"));
    }
}
