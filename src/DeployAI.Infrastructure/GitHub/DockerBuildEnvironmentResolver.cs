namespace DeployAI.Infrastructure.GitHub;

public static class DockerBuildEnvironmentResolver
{
    public static IReadOnlyDictionary<string, string> Resolve(
        string? dockerfileContent,
        string serviceDirectory,
        string? configuredDockerfilePath = null)
    {
        var normalizedServiceDirectory = string.IsNullOrWhiteSpace(serviceDirectory)
            ? string.Empty
            : serviceDirectory.Trim().Trim('/');

        var layout = DockerfileBuildAnalyzer.ResolveDockerBuildLayout(dockerfileContent, normalizedServiceDirectory);
        var entries = new Dictionary<string, string>
        {
            ["rootDirectory"] = layout.RootDirectory,
            ["dockerUsesRepositoryRoot"] = string.Equals(layout.RootDirectory, ".", StringComparison.Ordinal)
                ? "true"
                : "false"
        };

        if (!string.IsNullOrEmpty(normalizedServiceDirectory))
        {
            entries["serviceDirectory"] = normalizedServiceDirectory;
        }

        if (string.Equals(layout.RootDirectory, ".", StringComparison.Ordinal))
        {
            entries["dockerfilePath"] = layout.DockerfilePath;
        }
        else if (!string.IsNullOrWhiteSpace(configuredDockerfilePath) &&
                 !configuredDockerfilePath.Contains('/', StringComparison.Ordinal))
        {
            entries["dockerfilePath"] = configuredDockerfilePath.Trim().Trim('/');
        }
        else
        {
            entries["dockerfilePath"] = layout.DockerfilePath;
        }

        return entries;
    }
}
