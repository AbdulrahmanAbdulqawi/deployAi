using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;

namespace DeployAI.Infrastructure.GitHub;

/// <summary>
/// What a repository's deployment graph looks like, and how far the scan got.
/// </summary>
/// <param name="ComposePath">The compose file the graph came from; null when the repo has none.</param>
/// <param name="IsInconclusive">The scan could not see enough to answer. An empty graph with this
/// set means "nobody could look"; an empty graph without it means "this repository declares no
/// compose services", and the two must not be treated alike.</param>
/// <param name="UnreadableDirectories">Build contexts that could not be read. Their services are
/// still in the graph — they exist — but their frameworks are unknown rather than absent.</param>
/// <param name="Compose">The parsed compose file the graph came from; null when the repo has none.
/// Kept alongside the graph because some readiness rules are about what the file says rather than
/// about the deployment it describes — a published host port is a fact of the YAML.</param>
public sealed record RepositoryGraphScan(
    DeploymentGraph Graph,
    string? ComposePath,
    bool IsInconclusive,
    IReadOnlyList<string> UnreadableDirectories,
    string? Reason,
    ComposeFile? Compose = null)
{
    /// <summary>Whether this scan actually read a compose file and can be judged.</summary>
    public bool HasCompose => !IsInconclusive && Compose is not null && ComposePath is not null;

    public static RepositoryGraphScan Inconclusive(string reason) =>
        new(DeploymentGraph.Empty, null, true, [], reason);
}

public interface IRepositoryGraphScanner
{
    Task<RepositoryGraphScan> ScanAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string? composePathOverride,
        CancellationToken cancellationToken);
}

/// <summary>
/// Produces a <see cref="DeploymentGraph"/> from a repository.
/// </summary>
/// <remarks>
/// This is the entry point the graph path never had. <c>ComposeGenerator</c> and
/// <c>CoolifyComposePlanner</c> have been able to render and order a graph since they were
/// written; nothing could produce one, so the framework-agnostic path ran only in its own tests
/// while the shipping path stayed keyed to Angular plus .NET. Composition only — every rule it
/// relies on is tested in the piece that owns it.
/// </remarks>
public sealed class RepositoryGraphScanner(
    IRepositoryLayoutResolver layoutResolver,
    IRepositoryReader reader,
    IComposeSignalsReader signalsReader,
    IFrameworkAdapterFactory adapters) : IRepositoryGraphScanner
{
    /// <summary>
    /// Generated first, the repository's own last. A repo's <c>docker-compose.yml</c> is usually
    /// its local development stack, which publishes host ports Traefik expects to own — the same
    /// order the compose readiness evaluator uses, for the same reason.
    /// </summary>
    private static readonly string[] ComposeFileCandidates =
    [
        "docker-compose.coolify.yml",
        "docker-compose.coolify.yaml",
        "docker-compose.yml",
        "docker-compose.yaml"
    ];

    public async Task<RepositoryGraphScan> ScanAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string? composePathOverride,
        CancellationToken cancellationToken)
    {
        var layout = await layoutResolver.ResolveAsync(token, owner, repo, branch, null, cancellationToken);

        var candidates = string.IsNullOrWhiteSpace(composePathOverride)
            ? ComposeFileCandidates
            : [composePathOverride.Trim().TrimStart('/')];

        RepositoryFile? composeFile = null;
        foreach (var candidate in candidates)
        {
            composeFile = await reader.FindAsync(token, owner, repo, branch, layout, candidate, cancellationToken);
            if (composeFile is not null)
            {
                break;
            }
        }

        if (composeFile is null)
        {
            // Nothing found — but if the layout listed no directories, nothing was looked at
            // either, and reporting "this repository has no compose file" would be a claim the
            // scan is in no position to make.
            return layout.IsInconclusive
                ? RepositoryGraphScan.Inconclusive(
                    "The repository could not be listed, so whether it has a compose file is unknown.")
                : new RepositoryGraphScan(DeploymentGraph.Empty, null, false, [], null);
        }

        var compose = ComposeFileReader.Read(composeFile.Content);
        if (compose.IsInconclusive)
        {
            return RepositoryGraphScan.Inconclusive(
                $"`{composeFile.Path}` could not be parsed: {compose.ParseError}");
        }

        // Only built services have a context; a pulled image (the in-compose database) has none.
        var directories = compose.Services
            .Where(service => !string.IsNullOrWhiteSpace(service.BuildContext))
            // The builder's own rule, not a copy of it: the dictionary must be keyed by exactly
            // what the builder will look up.
            .Select(service => ComposeGraphBuilder.NormalizeContext(service.BuildContext))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var signals = await signalsReader.ReadAsync(token, owner, repo, branch, directories, cancellationToken);
        var graph = ComposeGraphBuilder.Build(compose, signals.SignalsByDirectory, adapters.All);

        return new RepositoryGraphScan(
            graph,
            composeFile.Path,
            IsInconclusive: false,
            signals.UnreadableDirectories,
            Reason: null,
            Compose: compose);
    }

}
