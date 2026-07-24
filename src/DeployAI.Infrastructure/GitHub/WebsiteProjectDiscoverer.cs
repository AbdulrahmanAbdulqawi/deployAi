namespace DeployAI.Infrastructure.GitHub;

public static class WebsiteProjectDiscoverer
{
    private static readonly string[] PreferredDirectoryNames =
    [
        "client",
        "web",
        "frontend",
        "app"
    ];

    // Directories that hold projects rather than being one — a monorepo nests the web app
    // under apps/web, packages/web, etc. We descend into these to find the real frontend.
    private static readonly string[] ContainerDirectoryNames =
    [
        "apps",
        "packages",
        "projects",
        "services",
        "src"
    ];

    public static bool ShouldScanNestedSubdirectories(string directoryPath) =>
        ContainerDirectoryNames.Any(container =>
            string.Equals(LeafName(directoryPath), container, StringComparison.OrdinalIgnoreCase));

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

    private static string LeafName(string path) =>
        path.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

    public static IReadOnlyList<string> RankCandidates(IEnumerable<string> directoryNames)
    {
        var ranked = directoryNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ScoreDirectoryName)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ranked;
    }

    public static IReadOnlyList<string> AllCandidatePaths(IEnumerable<string> directoryNames)
    {
        var paths = new List<string> { string.Empty };
        paths.AddRange(RankCandidates(directoryNames));
        return paths;
    }

    public static bool HasWebsiteSignals(IEnumerable<GitHubContentItem> contents)
    {
        foreach (var item in contents)
        {
            if (!string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(item.Name, "angular.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "index.html", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "package.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "next.config.js", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "next.config.mjs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "next.config.ts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "nuxt.config.ts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "astro.config.mjs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "svelte.config.js", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static int ScoreDirectoryName(string name)
    {
        var score = 0;

        for (var index = 0; index < PreferredDirectoryNames.Length; index++)
        {
            if (string.Equals(name, PreferredDirectoryNames[index], StringComparison.OrdinalIgnoreCase))
            {
                score += 80 - index;
                break;
            }
        }

        if (name.Contains("client", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (name.Contains("web", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("frontend", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }
}
