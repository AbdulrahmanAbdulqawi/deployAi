namespace DeployAI.Infrastructure.GitHub;

public static class ServerProjectDiscoverer
{
    private static readonly string[] PreferredDirectoryNames =
    [
        "server",
        "api",
        "backend",
        "src"
    ];

    private static readonly string[] FrontendDirectoryNames =
    [
        "client",
        "web",
        "frontend",
        "app"
    ];

    private static readonly string[] ContainerDirectoryNames =
    [
        "src",
        "backend",
        "server",
        "apps"
    ];

    public static IReadOnlyList<string> RankCandidates(IEnumerable<string> directoryNames)
    {
        return directoryNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !IsKnownFrontendDirectory(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ScoreDirectoryName)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> ExpandNestedCandidates(string parentDirectory, IEnumerable<string> subdirectoryNames)
    {
        if (!ShouldScanNestedSubdirectories(parentDirectory))
        {
            return [];
        }

        var normalizedParent = parentDirectory.Trim().Trim('/');
        return RankCandidates(subdirectoryNames)
            .Select(name => string.IsNullOrEmpty(normalizedParent) ? name : $"{normalizedParent}/{name}")
            .ToList();
    }

    public static bool IsKnownFrontendDirectory(string name) =>
        FrontendDirectoryNames.Any(frontend =>
            string.Equals(name, frontend, StringComparison.OrdinalIgnoreCase));

    public static bool ShouldScanNestedSubdirectories(string directoryPath) =>
        ContainerDirectoryNames.Any(container =>
            string.Equals(LeafName(directoryPath), container, StringComparison.OrdinalIgnoreCase));

    // The recursion passes full paths ("backend/src"); container matching is on the leaf.
    private static string LeafName(string path) =>
        path.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

    public static bool HasServerSignals(IEnumerable<GitHubContentItem> contents)
    {
        foreach (var item in contents)
        {
            if (!string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(item.Name, "Dockerfile", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "package.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "requirements.txt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "go.mod", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "Cargo.toml", StringComparison.OrdinalIgnoreCase) ||
                item.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static int ScoreDirectoryName(string name)
    {
        var score = 0;

        if (name.EndsWith(".Server", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }
        else if (name.EndsWith(".Api", StringComparison.OrdinalIgnoreCase))
        {
            score += 90;
        }

        for (var index = 0; index < PreferredDirectoryNames.Length; index++)
        {
            if (string.Equals(name, PreferredDirectoryNames[index], StringComparison.OrdinalIgnoreCase))
            {
                score += 80 - index;
                break;
            }
        }

        if (name.Contains("server", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (name.Contains("api", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }
}
