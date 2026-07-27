namespace DeployAI.Api.Services;

/// <summary>Guards which file paths Claude is allowed to write during setup/fix generation - rejects path traversal and any extension outside an allowlist, so a generated file can't overwrite something unexpected.</summary>
internal static class GeneratedDeploymentFilePathRules
{
    private static readonly string[] AllowedExtensions =
    [
        ".json", ".mjs", ".ts", ".tsx", ".js", ".jsx", ".cs", ".csproj", ".esproj",
        ".html", ".scss", ".css", ".toml", ".sln", ".props", ".targets", ".xml",
        ".yaml", ".yml", ".razor", ".md", ".dockerignore"
    ];

    /// <summary>Whether a generated file's path is safe to write: no path traversal, and an allowlisted extension or a Dockerfile.</summary>
    internal static bool IsAllowedPath(string path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
        {
            return false;
        }

        var fileName = Path.GetFileName(normalized);
        if (IsDockerfileName(fileName))
        {
            return true;
        }

        return AllowedExtensions.Any(ext => normalized.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return normalized;
    }

    private static bool IsDockerfileName(string fileName) =>
        string.Equals(fileName, "Dockerfile", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase);
}
