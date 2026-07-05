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
            return new ServerBuildProfile(
                normalizedRoot,
                null,
                null,
                null,
                "dotnet");
        }

        if (!string.IsNullOrWhiteSpace(packageJson))
        {
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
