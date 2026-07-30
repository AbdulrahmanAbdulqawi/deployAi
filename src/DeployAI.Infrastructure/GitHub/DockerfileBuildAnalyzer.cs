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

    /// <summary>
    /// Where the application's own source actually lives, for a Dockerfile whose build context is
    /// wider than that — distinct from <see cref="ResolveDockerBuildLayout"/>'s <c>RootDirectory</c>,
    /// which describes where Docker builds from, not where <c>appsettings.json</c> or
    /// <c>Program.cs</c> sit.
    /// </summary>
    /// <remarks>
    /// A multi-stage Dockerfile that <c>COPY</c>s the whole repository and publishes a nested
    /// project reports its build context as the repository root — correctly, that is where
    /// <c>docker build</c> has to run — but every caller that goes looking for the app's own config
    /// afterward (database requirements, missing-settings checks) was handed that same root and
    /// found nothing there, because the actual project is nested. Mirqab's api service builds this
    /// way: root context, `COPY src/Mirqab.Api/Mirqab.Api.csproj src/Mirqab.Api/`, and an entry
    /// point of `Mirqab.Api.dll`. Detection searched the repository root for appsettings.json,
    /// found nothing, and reported no database requirement for an app that fails on its first line
    /// without one.
    /// <para>
    /// Resolved by identity, not by position: the <c>ENTRYPOINT</c>/<c>CMD</c> names the published
    /// assembly, and exactly one <c>COPY</c>'d <c>.csproj</c> shares that name. "The last COPY" or
    /// "the deepest path" would guess; matching the name the image itself declares it runs does
    /// not. Returns null rather than guess when there is no exact match — a single .csproj build
    /// has no entry to resolve and does not need one.
    /// </para>
    /// </remarks>
    public static string? ResolveEntryProjectDirectory(string? dockerfileContent)
    {
        if (string.IsNullOrWhiteSpace(dockerfileContent))
        {
            return null;
        }

        var entryAssemblyName = ExtractEntryAssemblyName(dockerfileContent);
        if (entryAssemblyName is null)
        {
            return null;
        }

        foreach (var sourcePath in ExtractCopySourcePaths(dockerfileContent))
        {
            if (!sourcePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = sourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
            var projectName = fileName[..^".csproj".Length];
            if (string.Equals(projectName, entryAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                var lastSlash = sourcePath.LastIndexOf('/');
                return lastSlash < 0 ? string.Empty : sourcePath[..lastSlash];
            }
        }

        return null;
    }

    /// <summary>The assembly ENTRYPOINT/CMD runs, read from <c>dotnet X.dll</c> — the name a published .NET image always runs by.</summary>
    internal static string? ExtractEntryAssemblyName(string dockerfileContent)
    {
        var match = EntrypointDllRegex().Match(dockerfileContent);
        return match.Success ? match.Groups["assembly"].Value : null;
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

    // Matches both exec-form ENTRYPOINT/CMD (["dotnet", "X.dll"]) and the shell form (dotnet X.dll).
    [GeneratedRegex(
        """^\s*(?:ENTRYPOINT|CMD)\s+\[?\s*["']?dotnet["']?[\s,]+["']?(?<assembly>[\w.-]+)\.dll""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex EntrypointDllRegex();
}
