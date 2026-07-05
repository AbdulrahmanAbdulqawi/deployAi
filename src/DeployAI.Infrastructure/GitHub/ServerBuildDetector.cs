using System.Text.Json;
using DeployAI.Core.Deployments;

namespace DeployAI.Infrastructure.GitHub;

public interface IServerBuildDetector
{
    ServerBuildProfile Detect(
        string rootDirectory,
        bool hasDockerfile,
        string? dockerfileContent,
        string? packageJson,
        string? csprojContent);
}

public sealed class ServerBuildDetector : IServerBuildDetector
{
    public ServerBuildProfile Detect(
        string rootDirectory,
        bool hasDockerfile,
        string? dockerfileContent,
        string? packageJson,
        string? csprojContent)
    {
        var normalizedRoot = NormalizeRootDirectory(rootDirectory);

        if (hasDockerfile)
        {
            if (DockerfileBuildAnalyzer.RequiresRepositoryRoot(dockerfileContent, normalizedRoot))
            {
                return new ServerBuildProfile(
                    string.Empty,
                    null,
                    null,
                    null,
                    "docker",
                    DockerfileBuildAnalyzer.BuildDockerfilePath(normalizedRoot),
                    normalizedRoot);
            }

            return new ServerBuildProfile(normalizedRoot, null, null, null, "docker", null, normalizedRoot);
        }

        if (!string.IsNullOrWhiteSpace(csprojContent))
        {
            var serviceDirectory = normalizedRoot;
            var buildRootDirectory = normalizedRoot;

            if (CsprojBuildAnalyzer.HasSiblingProjectReferences(csprojContent))
            {
                buildRootDirectory = CsprojBuildAnalyzer.ResolveBuildRootDirectory(normalizedRoot);
            }

            var buildCommand = CsprojBuildAnalyzer.BuildPublishCommand(buildRootDirectory, serviceDirectory);

            return new ServerBuildProfile(
                buildRootDirectory,
                buildCommand,
                null,
                null,
                "dotnet",
                null,
                serviceDirectory);
        }

        if (!string.IsNullOrWhiteSpace(packageJson))
        {
            if (IsFrontendSpaPackageJson(packageJson))
            {
                return new ServerBuildProfile(normalizedRoot, null, null, null, null);
            }

            var (buildCommand, startCommand) = ParseNodeScripts(packageJson);
            return new ServerBuildProfile(
                normalizedRoot,
                buildCommand,
                "npm install",
                startCommand ?? "npm start",
                "node");
        }

        return new ServerBuildProfile(normalizedRoot, null, null, null, null);
    }

    internal static bool IsFrontendSpaPackageJson(string packageJson)
    {
        if (FrontendBuildDetector.DetectFramework(null, packageJson) is not null)
        {
            return true;
        }

        return HasDevOnlyStartScript(packageJson);
    }

    internal static bool HasDevOnlyStartScript(string packageJson)
    {
        try
        {
            using var document = JsonDocument.Parse(packageJson);
            if (!document.RootElement.TryGetProperty("scripts", out var scripts) ||
                !scripts.TryGetProperty("start", out var startScript))
            {
                return false;
            }

            var start = startScript.GetString();
            if (string.IsNullOrWhiteSpace(start))
            {
                return false;
            }

            return start.Contains("ng serve", StringComparison.OrdinalIgnoreCase) ||
                   start.Contains("vite", StringComparison.OrdinalIgnoreCase) ||
                   start.Contains("next dev", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static (string? BuildCommand, string? StartCommand) ParseNodeScripts(string packageJson)
    {
        try
        {
            using var document = JsonDocument.Parse(packageJson);
            if (!document.RootElement.TryGetProperty("scripts", out var scripts))
            {
                return (null, null);
            }

            string? buildCommand = scripts.TryGetProperty("build", out _)
                ? "npm run build"
                : null;
            string? startCommand = scripts.TryGetProperty("start", out _)
                ? "npm start"
                : scripts.TryGetProperty("dev", out _)
                    ? "npm run dev"
                    : null;

            return (buildCommand, startCommand);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string NormalizeRootDirectory(string rootDirectory) =>
        string.IsNullOrWhiteSpace(rootDirectory) ? string.Empty : rootDirectory.Trim().Trim('/');
}
