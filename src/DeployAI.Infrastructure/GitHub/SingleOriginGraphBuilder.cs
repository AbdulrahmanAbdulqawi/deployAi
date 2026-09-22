using DeployAI.Core.Deployments;
using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Core.Deployments.Graph.Transforms;

namespace DeployAI.Infrastructure.GitHub;

/// <summary>
/// Builds the single-origin deployment graph for a website + server plan, for a repository that
/// has no compose file yet.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ComposeGraphBuilder"/> reads a graph out of a compose file the repository already
/// wrote. Setup is the other direction: the repository has nothing, and the plan's two parts are
/// the whole statement of what to deploy. Both reach a node the same way — through
/// <see cref="ComposeGraphBuilder.BuildNodeForDirectory"/> — so a directory is read identically
/// whichever path arrives at it.
/// </para>
/// <para>
/// The service ids are fixed, and deliberately: <c>nginx.conf</c> proxies to the API by service
/// name over the compose network, the existing readiness gate looks for <c>api</c> and <c>web</c>,
/// and every app already deployed from the templates runs containers with those names. Generating
/// different ones would rename the containers on the next deploy for no benefit.
/// </para>
/// </remarks>
public static class SingleOriginGraphBuilder
{
    public const string WebServiceId = "web";
    public const string ApiServiceId = "api";

    /// <summary>
    /// What the templates have always written. nginx's own default is 1 MB, which rejects real
    /// uploads with a 413 at the proxy before the API ever sees the request.
    /// </summary>
    public const string DefaultMaxBodySize = "25m";

    public static DeploymentGraph Build(
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart,
        IReadOnlyDictionary<string, RepositorySignals> signalsByDirectory,
        IReadOnlyList<IFrameworkAdapter> adapters)
    {
        var graph = DeploymentGraph.Empty
            .WithService(ComposeGraphBuilder.BuildNodeForDirectory(
                WebServiceId, websitePart.RootDirectory, signalsByDirectory, adapters))
            .WithService(ComposeGraphBuilder.BuildNodeForDirectory(
                ApiServiceId, serverPart.RootDirectory, signalsByDirectory, adapters));

        // A no-op when the pair is ambiguous — which is why the caller must check rather than
        // assume. An un-transformed graph has no ingress and no proxy route: it would render a
        // compose file that deploys green and answers nothing.
        return SingleOriginTransform.Apply(
            graph,
            new SingleOriginTransform.Options(MaxBodySize: DefaultMaxBodySize));
    }

    /// <summary>
    /// The directories that must be read before <see cref="Build"/> can recognise either part.
    /// </summary>
    public static IReadOnlyList<string> DirectoriesToRead(
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart) =>
    [
        ..new[] { websitePart.RootDirectory, serverPart.RootDirectory }
            .Select(ComposeGraphBuilder.NormalizeContext)
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];
}
