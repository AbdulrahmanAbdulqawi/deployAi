namespace DeployAI.Infrastructure.GitHub;

public static class CsprojBuildAnalyzer
{
    public static bool HasSiblingProjectReferences(string? csprojContent) =>
        !string.IsNullOrWhiteSpace(csprojContent) &&
        csprojContent.Contains("ProjectReference Include=\"..", StringComparison.Ordinal);

    public static string ResolveBuildRootDirectory(string projectDirectory)
    {
        var normalized = NormalizeDirectory(projectDirectory);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : normalized[..lastSlash];
    }

    public static string? BuildPublishCommand(string buildRootDirectory, string serviceDirectory)
    {
        var normalizedService = NormalizeDirectory(serviceDirectory);
        var normalizedRoot = NormalizeDirectory(buildRootDirectory);
        if (string.IsNullOrEmpty(normalizedService) ||
            string.Equals(normalizedRoot, normalizedService, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var projectFolder = normalizedService.Split('/').Last();
        if (string.IsNullOrEmpty(projectFolder))
        {
            return null;
        }

        return $"dotnet publish {projectFolder}/{projectFolder}.csproj -c Release -o out";
    }

    private static string NormalizeDirectory(string directory) =>
        string.IsNullOrWhiteSpace(directory) ? string.Empty : directory.Trim().Trim('/');
}
