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
        string? csprojContent,
        string? requirementsTxt = null,
        string? pyprojectToml = null,
        string? goMod = null,
        string? cargoToml = null);
}

public sealed class ServerBuildDetector : IServerBuildDetector
{
    public ServerBuildProfile Detect(
        string rootDirectory,
        bool hasDockerfile,
        string? dockerfileContent,
        string? packageJson,
        string? csprojContent,
        string? requirementsTxt = null,
        string? pyprojectToml = null,
        string? goMod = null,
        string? cargoToml = null)
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
            var startCommand = CsprojBuildAnalyzer.BuildStartCommand(buildRootDirectory, serviceDirectory);

            return new ServerBuildProfile(
                buildRootDirectory,
                buildCommand,
                null,
                startCommand,
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

        if (!string.IsNullOrWhiteSpace(requirementsTxt) || !string.IsNullOrWhiteSpace(pyprojectToml))
        {
            var installCommand = !string.IsNullOrWhiteSpace(requirementsTxt)
                ? "pip install -r requirements.txt"
                : "pip install .";
            var startCommand = InferPythonStartCommand(requirementsTxt, pyprojectToml);
            return new ServerBuildProfile(
                normalizedRoot,
                null,
                installCommand,
                startCommand,
                "python");
        }

        if (!string.IsNullOrWhiteSpace(goMod))
        {
            return new ServerBuildProfile(
                normalizedRoot,
                "go build -o app .",
                null,
                "./app",
                "go");
        }

        if (!string.IsNullOrWhiteSpace(cargoToml))
        {
            var binaryName = ExtractCargoBinaryName(cargoToml);
            var startCommand = string.IsNullOrWhiteSpace(binaryName)
                ? "./target/release/app"
                : $"./target/release/{binaryName}";
            return new ServerBuildProfile(
                normalizedRoot,
                "cargo build --release",
                null,
                startCommand,
                "rust");
        }

        return new ServerBuildProfile(normalizedRoot, null, null, null, null);
    }

    internal static string InferPythonStartCommand(string? requirementsTxt, string? pyprojectToml)
    {
        var haystack = $"{requirementsTxt}\n{pyprojectToml}";
        if (haystack.Contains("uvicorn", StringComparison.OrdinalIgnoreCase))
        {
            return "uvicorn main:app --host 0.0.0.0 --port $PORT";
        }

        if (haystack.Contains("gunicorn", StringComparison.OrdinalIgnoreCase))
        {
            return "gunicorn main:app --bind 0.0.0.0:$PORT";
        }

        if (haystack.Contains("flask", StringComparison.OrdinalIgnoreCase))
        {
            return "python -m flask run --host 0.0.0.0 --port $PORT";
        }

        return "python main.py";
    }

    internal static string? ExtractCargoBinaryName(string cargoToml)
    {
        foreach (var line in cargoToml.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("name", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Contains('='))
            {
                var value = trimmed[(trimmed.IndexOf('=') + 1)..].Trim().Trim('"', '\'');
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
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
