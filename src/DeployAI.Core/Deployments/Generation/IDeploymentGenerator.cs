using DeployAI.Core.Deployments.Graph;

namespace DeployAI.Core.Deployments.Generation;

/// <summary>
/// Renders a deployment graph into target files. Rendering only — every decision (what to
/// create, in what order, reuse or not) belongs to the planner. A generator that starts
/// making decisions is how target knowledge leaks back into the wrong layer.
/// </summary>
public interface IDeploymentGenerator
{
    /// <summary>The orchestrator this generator renders for ("compose"; later "kubernetes", "nomad").</summary>
    string Target { get; }

    GeneratedArtifacts Generate(DeploymentGraph graph);
}

public sealed record GeneratedArtifacts(IReadOnlyList<GeneratedArtifact> Files)
{
    public GeneratedArtifact? Find(string path) =>
        Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
}

public sealed record GeneratedArtifact(
    /// <summary>Repo-relative path ("docker-compose.coolify.yml", "client/nginx.conf").</summary>
    string Path,
    string Content);
