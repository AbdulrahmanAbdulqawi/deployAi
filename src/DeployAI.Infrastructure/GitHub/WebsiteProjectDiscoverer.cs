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
