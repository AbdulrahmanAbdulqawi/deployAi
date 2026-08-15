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
            var layout = DockerfileBuildAnalyzer.ResolveDockerBuildLayout(dockerfileContent, normalizedRoot);
            var profileRootDirectory = string.Equals(layout.RootDirectory, ".", StringComparison.Ordinal)
                ? string.Empty
                : layout.RootDirectory;
            var dockerfilePath = string.Equals(layout.RootDirectory, ".", StringComparison.Ordinal)
                ? layout.DockerfilePath
                : null;

            // ServiceDirectory answers a different question than the build layout above: not where
            // Docker builds from, but where the application's own source sits, for callers that go
            // looking for its config afterward (appsettings.json, database requirements). Those are
            // the same directory for a single-project build, and different whenever the build
            // context is wider than the project it publishes — a multi-stage Dockerfile that COPYs
            // the whole repository and builds a nested project reports normalizedRoot ("", the
            // repository root) here unless the entry project can be resolved from the Dockerfile
            // itself, which is exactly the case this exists for.
            var serviceDirectory = DockerfileBuildAnalyzer.ResolveEntryProjectDirectory(dockerfileContent)
                ?? normalizedRoot;

            return new ServerBuildProfile(
                profileRootDirectory,
                null,
                null,
                null,
                "docker",
                dockerfilePath,
                serviceDirectory,
                string.Equals(layout.RootDirectory, ".", StringComparison.Ordinal));
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
