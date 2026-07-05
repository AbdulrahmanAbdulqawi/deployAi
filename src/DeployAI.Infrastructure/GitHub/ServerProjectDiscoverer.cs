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

    public static IReadOnlyList<string> RankCandidates(IEnumerable<string> directoryNames)
    {
        return directoryNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ScoreDirectoryName)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
