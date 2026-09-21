using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Deployments.Generation;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Core.Deployments.Graph.Transforms;
using DeployAI.Core.Deployments.Adapters;
using DeployAI.Infrastructure.Adapters;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.Graph;

/// <summary>
/// The parity question M1 turns on: if the graph path replaces the Angular-plus-.NET compose
/// templates, does what it writes still satisfy the gate those templates were written against?
///
/// These build the graph the way production now can — read a compose file, build nodes, apply the
/// single-origin transform — render it, and then judge the result with
/// <c>SingleOriginComposeReadinessEvaluator</c>, the evaluator that is Blocking today. If the
/// generated output passes the old rules, switching the generator is safe; if it does not, this
/// says exactly which rule it breaks, before any repository finds out.
/// </summary>
public class ComposeGeneratorParityTests
{
    private const string AngularPackageJson = """
        { "dependencies": { "@angular/core": "19.0.0" }, "scripts": { "build": "npm run build" } }
        """;

    private const string AngularJson = """
        { "projects": { "client": { "architect": { "build": { "options": { "outputPath": "dist/client" } } } } } }
        """;

    private const string Csproj =
        "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";

    /// <summary>The shape the templates exist for: an Angular bundle and a .NET API, one origin.</summary>
    private const string YemeniBreezeCompose = """
        services:
          api:
            build: ./server
            expose:
              - '8080'
          web:
            build: ./client
            expose:
              - '80'
            depends_on:
              - api
        """;

    private static GeneratedArtifacts GenerateFromRepository()
    {
        var compose = ComposeFileReader.Read(YemeniBreezeCompose);
        var signals = new Dictionary<string, RepositorySignals>(StringComparer.OrdinalIgnoreCase)
        {
            ["client"] = new RepositorySignals("client", PackageJson: AngularPackageJson, AngularJson: AngularJson),
            ["server"] = new RepositorySignals("server", CsprojContent: Csproj, CsprojFileName: "Breeze.Api.csproj")
        };

        var graph = ComposeGraphBuilder.Build(
            compose,
            signals,
            [new AngularAdapter(), new DotnetAdapter()]);

        // The transform is what turns "a bundle and an API" into "one origin": it marks the
        // static site as the deployment's reverse proxy and adds the /api route.
        graph = SingleOriginTransform.Apply(
            graph,
            new SingleOriginTransform.Options(Domain: "breeze.example.com", MaxBodySize: "210m", WebSockets: true));

        return new ComposeGenerator().Generate(graph);
    }

    [Fact]
    public void GeneratedOutput_SatisfiesTheEvaluatorTheTemplatesWereWrittenFor()
    {
        var artifacts = GenerateFromRepository();
        var files = artifacts.Files.ToDictionary(f => f.Path, f => (string?)f.Content, StringComparer.OrdinalIgnoreCase);

        var website = new DeploymentPlanPart("website", "coolify") { RootDirectory = "client" };
        var server = new DeploymentPlanPart("server", "coolify") { RootDirectory = "server" };

        var findings = SingleOriginComposeReadinessEvaluator.Evaluate(website, server, files);

        var blocking = findings.Where(f => f.Severity == DeploymentFileSeverity.Blocking).ToList();
        Assert.True(
            blocking.Count == 0,
            "Graph-generated output would be refused by the current gate:\n" +
            string.Join("\n", blocking.Select(f => $"  {f.Path}: {f.Reason}")));
    }

    /// <summary>
    /// Guards the test above from being vacuous. A parity check that passes because the evaluator
    /// never refuses anything proves nothing at all, so this shows it refusing the empty case —
    /// if this ever goes green, the parity assertion has stopped meaning anything.
    /// </summary>
    [Fact]
    public void TheGateThisIsCheckedAgainst_DoesRefuseOutputThatIsMissing()
    {
        var website = new DeploymentPlanPart("website", "coolify") { RootDirectory = "client" };
        var server = new DeploymentPlanPart("server", "coolify") { RootDirectory = "server" };

        var findings = SingleOriginComposeReadinessEvaluator.Evaluate(
            website,
            server,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains(findings, f => f.Severity == DeploymentFileSeverity.Blocking);
    }

    /// <summary>
    /// The three files the templates produce, at the paths the evaluator looks for them. A
    /// generator that wrote the right content to the wrong path would pass a content check and
    /// fail a real deploy.
    /// </summary>
    [Fact]
    public void GeneratedOutput_WritesTheSameFilesAtTheSamePaths()
    {
        var artifacts = GenerateFromRepository();

        Assert.NotNull(artifacts.Find("docker-compose.coolify.yml"));
        Assert.NotNull(artifacts.Find("client/Dockerfile"));
        Assert.NotNull(artifacts.Find("client/nginx.conf"));
        Assert.NotNull(artifacts.Find("server/Dockerfile"));
    }

    /// <summary>
    /// Regenerating an unchanged graph must produce byte-identical output, or every deploy appends
    /// a commit that changes nothing to someone's history — the incident behind the idempotence
    /// rule for files DeployAI writes into a user's repository.
    /// </summary>
    [Fact]
    public void GeneratedOutput_IsByteIdenticalOnASecondRun()
    {
        var first = GenerateFromRepository();
        var second = GenerateFromRepository();

        Assert.Equal(
            first.Files.Select(f => (f.Path, f.Content)),
            second.Files.Select(f => (f.Path, f.Content)));
    }
}
