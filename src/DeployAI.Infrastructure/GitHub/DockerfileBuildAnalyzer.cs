using System.Text.RegularExpressions;

namespace DeployAI.Infrastructure.GitHub;

public static partial class DockerfileBuildAnalyzer
{
    public readonly record struct DockerBuildLayout(string RootDirectory, string DockerfilePath);

    public static bool RequiresRepositoryRoot(string? dockerfileContent, string serviceDirectory) =>
        string.Equals(
            ResolveDockerBuildLayout(dockerfileContent, serviceDirectory).RootDirectory,
            ".",
            StringComparison.Ordinal);

    public static DockerBuildLayout ResolveDockerBuildLayout(string? dockerfileContent, string serviceDirectory)
    {
        var normalizedServiceDirectory = NormalizeDirectory(serviceDirectory);

        if (string.IsNullOrWhiteSpace(dockerfileContent))
        {
            return ServiceDirectoryLayout(normalizedServiceDirectory);
        }

        foreach (var sourcePath in ExtractCopySourcePaths(dockerfileContent))
        {
            if (sourcePath.Contains("../", StringComparison.Ordinal))
            {
                return RepositoryRootLayout(normalizedServiceDirectory);
            }
        }

        var copySegments = ExtractCopySourcePaths(dockerfileContent)
            .Select(GetFirstPathSegment)
            .Where(segment => !string.IsNullOrEmpty(segment) && !string.Equals(segment, ".", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (copySegments.Count == 0)
        {
            return ServiceDirectoryLayout(normalizedServiceDirectory);
        }

        if (!string.IsNullOrEmpty(normalizedServiceDirectory) &&
            copySegments.Any(segment =>
                string.Equals(segment, normalizedServiceDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            return RepositoryRootLayout(normalizedServiceDirectory);
        }

        return ServiceDirectoryLayout(normalizedServiceDirectory);
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

    private static DockerBuildLayout RepositoryRootLayout(string normalizedServiceDirectory) =>
        new(".", BuildDockerfilePath(normalizedServiceDirectory));

    private static DockerBuildLayout ServiceDirectoryLayout(string normalizedServiceDirectory) =>
        new(
            string.IsNullOrEmpty(normalizedServiceDirectory) ? "." : normalizedServiceDirectory,
            "Dockerfile");

    private static string GetFirstPathSegment(string sourcePath)
    {
        if (!sourcePath.Contains('/', StringComparison.Ordinal))
        {
            return sourcePath;
        }

        return sourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string NormalizeDirectory(string directory) =>
        string.IsNullOrWhiteSpace(directory) ? string.Empty : directory.Trim().Trim('/');

    [GeneratedRegex(@"^\s*(?:COPY|ADD)\s+\[(?<source>[^\],]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopyJsonSourceRegex();
}
