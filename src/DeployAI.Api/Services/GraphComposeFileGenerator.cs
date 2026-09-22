using DeployAI.Core.Deployments;
using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Generation;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Core.Deployments.Graph.Transforms;
using DeployAI.Core.Exceptions;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

/// <summary>
/// Generates a single-origin compose deployment from the graph instead of from framework-keyed
/// templates.
/// </summary>
/// <remarks>
/// <para>
/// Every layer of the template path is hardcoded to Angular plus .NET: the scenario name, the
/// framework match, and five files of literal YAML. The graph path contains no framework name at
/// all — an adapter claims a directory, the graph says what the deployment is, and
/// <see cref="ComposeGenerator"/> renders it. That the output still satisfies the gate the
/// templates were written for is not assumed here; it is pinned in
/// <c>ComposeGeneratorParityTests</c>.
/// </para>
/// <para>
/// Only the topology files are this generator's: the compose file, each service's Dockerfile, and
/// the proxy's nginx.conf. <c>docs/DEPLOYMENT.md</c> and a health controller are content, not
/// topology, and are still scaffolded from templates — dropping them would quietly lose two
/// Recommended files the previous path delivered.
/// </para>
/// </remarks>
public sealed class GraphComposeFileGenerator : IDeploymentFileGenerator
{
    private readonly IComposeSignalsReader _signalsReader;
    private readonly IFrameworkAdapterFactory _adapters;
    private readonly TemplateDeploymentFileGenerator _templates;
    private readonly ComposeGenerator _compose = new();

    public GraphComposeFileGenerator(
        IComposeSignalsReader signalsReader,
        IFrameworkAdapterFactory adapters,
        TemplateDeploymentFileGenerator templates)
    {
        _signalsReader = signalsReader;
        _adapters = adapters;
        _templates = templates;
    }

    public async Task<IReadOnlyList<GeneratedDeploymentFile>> GenerateMissingFilesAsync(
        string owner,
        string repo,
        string gitRef,
        string githubAccessToken,
        IReadOnlyList<DeploymentPlanPart> parts,
        IReadOnlyList<MissingDeploymentFile> missingFiles,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken)
    {
        var website = SplitOriginDetection.FindWebsitePart(parts);
        var server = SplitOriginDetection.FindServerPart(parts);
        if (website is null || server is null)
        {
            throw new DeployAIException(
                "setup_generation_failed",
                "A single-origin compose deployment needs both a website part and a server part.");
        }

        await ReportAsync(reportActivity, "Reading the repository to work out what this deployment is made of…");

        var directories = SingleOriginGraphBuilder.DirectoriesToRead(website, server);
        var signals = await _signalsReader.ReadAsync(
            githubAccessToken, owner, repo, gitRef, directories, cancellationToken);

        // Unreadable is not empty. Generating from signals nobody could read produces a compose
        // file whose services have no Dockerfile, which deploys green and fails at build — so the
        // refusal happens here, where it can still name the directory.
        if (signals.UnreadableDirectories.Count > 0)
        {
            throw new DeployAIException(
                "setup_generation_failed",
                $"Could not read {string.Join(" or ", signals.UnreadableDirectories.Select(Describe))} on `{gitRef}`, "
                + "so there is nothing to work out what to build there. Check the directories on the plan.");
        }

        var graph = SingleOriginGraphBuilder.Build(website, server, signals.SignalsByDirectory, _adapters.All);

        if (!SingleOriginTransform.CanApply(graph))
        {
            throw new DeployAIException("setup_generation_failed", DescribeAmbiguity(graph));
        }

        var artifacts = _compose.Generate(graph);
        var generated = artifacts.Files
            .Select(file => new GeneratedDeploymentFile(file.Path, file.Content))
            .ToList();

        await ReportAsync(
            reportActivity,
            $"Generated {generated.Count} deployment file(s) from the repository's own structure: "
            + string.Join(", ", generated.Select(file => file.Path)));

        // Whatever the graph did not write is still wanted — the docs and health-endpoint files the
        // template path produced. Asking by difference rather than by a remembered list means a file
        // the generator starts producing stops being asked for, without a second place to update.
        var remaining = missingFiles
            .Where(file => !generated.Any(g => string.Equals(g.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (remaining.Length == 0)
        {
            return generated;
        }

        var scaffolded = await _templates.GenerateMissingFilesAsync(
            owner, repo, gitRef, githubAccessToken, parts, remaining, reportActivity, cancellationToken);

        generated.AddRange(scaffolded.Where(file =>
            !generated.Any(g => string.Equals(g.Path, file.Path, StringComparison.OrdinalIgnoreCase))));

        return generated;
    }

    /// <summary>
    /// Says which part could not be placed and what was found instead. "Could not generate
    /// deployment files" is true and useless; the user can act on "nothing recognised `client`".
    /// </summary>
    private static string DescribeAmbiguity(DeploymentGraph graph)
    {
        var found = graph.Services
            .Select(node => $"`{node.Source.BuildContext ?? node.Id}` looks like "
                            + (node.Framework is null ? "nothing DeployAI recognises" : node.Framework))
            .ToArray();

        return "This deployment needs exactly one static site fronting exactly one API, and that is not "
               + $"what the repository shows: {string.Join("; ", found)}. Point the plan at the directories "
               + "that hold the site and the API, or add a Dockerfile to each.";
    }

    private static string Describe(string directory) =>
        string.IsNullOrEmpty(directory) ? "the repository root" : $"`{directory}`";

    private static Task ReportAsync(Func<string, Task>? reportActivity, string message) =>
        reportActivity is null ? Task.CompletedTask : reportActivity(message);
}
