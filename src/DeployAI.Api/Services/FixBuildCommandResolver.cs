using System.Text.RegularExpressions;
using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

/// <summary>Resolves the install/build/run commands a fix-generation build sandbox should use for a target, inferring the .csproj path from referenced files/Dockerfile when it isn't explicitly configured.</summary>
public static class FixBuildCommandResolver
{
    private static readonly Regex DotnetProjectRegex = new(
        @"dotnet\s+(?:build|publish|restore)\s+[""']?(?<path>[^""'\s]+\.csproj)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static FixBuildPlan Resolve(
        string providerName,
        DeployTargetConfig config,
        string? framework,
        IReadOnlyList<string> referencedFiles,
        string? dockerfileContent)
    {
        var isVercel = string.Equals(providerName, "vercel", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(config.Role, "website", StringComparison.OrdinalIgnoreCase);

        if (isVercel)
        {
            var root = NormalizeDir(config.RootDirectory) ?? ".";
            var install = string.IsNullOrWhiteSpace(config.InstallCommand) ? "npm install" : config.InstallCommand!;
            var build = string.IsNullOrWhiteSpace(config.BuildCommand) ? "npm run build" : config.BuildCommand!;
            return new FixBuildPlan(root, install, build);
        }

        var serviceDir = NormalizeDir(config.ServiceDirectory) ??
                         NormalizeDir(config.RootDirectory) ??
                         InferServiceDirectory(referencedFiles) ??
                         ".";

        var csproj = InferCsprojPath(config, referencedFiles, dockerfileContent);
        var csprojRelativeToWorkingDir = NormalizeCsprojForWorkingDirectory(csproj, serviceDir);
        var dotnetBuild = string.IsNullOrWhiteSpace(csprojRelativeToWorkingDir)
            ? "dotnet build"
            : $"dotnet build \"{csprojRelativeToWorkingDir}\"";

        _ = framework;
        return new FixBuildPlan(serviceDir, null, dotnetBuild);
    }

    internal static string? NormalizeCsprojForWorkingDirectory(string? csprojPath, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(csprojPath))
        {
            return null;
        }

        var path = csprojPath.Replace('\\', '/').Trim();
        if (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        var workingDir = NormalizeDir(workingDirectory) ?? ".";
        if (string.Equals(workingDir, ".", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith($"{workingDir}/", StringComparison.OrdinalIgnoreCase))
        {
            return path[(workingDir.Length + 1)..];
        }

        var fileName = path.Split('/').Last();
        if (string.Equals(path, $"{workingDir}/{fileName}", StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        return path.Contains('/', StringComparison.Ordinal) ? path : fileName;
    }

    private static string? InferServiceDirectory(IReadOnlyList<string> referencedFiles)
    {
        foreach (var path in referencedFiles)
        {
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0)
            {
                return path[..slash];
            }
        }

        return null;
    }

    private static string? InferCsprojPath(
        DeployTargetConfig config,
        IReadOnlyList<string> referencedFiles,
        string? dockerfileContent)
    {
        if (!string.IsNullOrWhiteSpace(dockerfileContent))
        {
            foreach (Match match in DotnetProjectRegex.Matches(dockerfileContent))
            {
                return match.Groups["path"].Value.Replace('\\', '/');
            }

            var layout = DockerfileBuildAnalyzer.ResolveDockerBuildLayout(
                dockerfileContent,
                config.ServiceDirectory ?? config.RootDirectory ?? string.Empty);
            if (!string.Equals(layout.RootDirectory, ".", StringComparison.Ordinal))
            {
                var dir = layout.RootDirectory;
                return $"{dir}/{dir.Split('/').Last()}.csproj";
            }
        }

        foreach (var path in referencedFiles)
        {
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        var serviceDir = NormalizeDir(config.ServiceDirectory) ?? NormalizeDir(config.RootDirectory);
        if (!string.IsNullOrWhiteSpace(serviceDir))
        {
            var folderName = serviceDir.Split('/').Last();
            return $"{serviceDir}/{folderName}.csproj";
        }

        return null;
    }

    private static string? NormalizeDir(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var normalized = directory.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
