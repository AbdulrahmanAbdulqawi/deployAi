using System.Text.RegularExpressions;

namespace DeployAI.Infrastructure.GitHub;

public static partial class DockerfileBuildAnalyzer
{
    public static bool RequiresRepositoryRoot(string? dockerfileContent, string serviceDirectory)
    {
        if (string.IsNullOrWhiteSpace(dockerfileContent))
        {
            return false;
        }

        var normalizedServiceDirectory = NormalizeDirectory(serviceDirectory);
        foreach (var sourcePath in ExtractCopySourcePaths(dockerfileContent))
        {
            if (sourcePath.Contains("../", StringComparison.Ordinal))
            {
                return true;
            }

            if (!sourcePath.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            var firstSegment = sourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            if (string.Equals(firstSegment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(normalizedServiceDirectory) &&
                string.Equals(firstSegment, normalizedServiceDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(normalizedServiceDirectory) &&
                !string.Equals(firstSegment, normalizedServiceDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string BuildDockerfilePath(string serviceDirectory) =>
        string.IsNullOrEmpty(NormalizeDirectory(serviceDirectory))
            ? "Dockerfile"
            : $"{NormalizeDirectory(serviceDirectory)}/Dockerfile";

    internal static IEnumerable<string> ExtractCopySourcePaths(string dockerfileContent)
    {
        foreach (var line in dockerfileContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("COPY", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("ADD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var jsonMatch = CopyJsonSourceRegex().Match(trimmed);
            if (jsonMatch.Success)
            {
                yield return jsonMatch.Groups[1].Value.Trim().Trim('"', '\'');
                continue;
            }

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length >= 2)
            {
                yield return tokens[1].Trim('"', '\'');
            }
        }
    }

    private static string NormalizeDirectory(string directory) =>
        string.IsNullOrWhiteSpace(directory) ? string.Empty : directory.Trim().Trim('/');

    [GeneratedRegex(@"^\s*(?:COPY|ADD)\s+\[(?<source>[^\],]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopyJsonSourceRegex();
}
