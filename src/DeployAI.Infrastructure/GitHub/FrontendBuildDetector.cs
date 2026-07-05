using System.Text.Json;
using DeployAI.Core.Deployments;

namespace DeployAI.Infrastructure.GitHub;

public interface IFrontendBuildDetector
{
    FrontendBuildProfile Detect(string rootDirectory, string? angularJson, string? packageJson);
}

public sealed class FrontendBuildDetector : IFrontendBuildDetector
{
    private static readonly (string Package, string Framework)[] FrameworkPackages =
    [
        ("next", "next"),
        ("nuxt", "nuxt"),
        ("@nuxt/kit", "nuxt"),
        ("@sveltejs/kit", "sveltekit"),
        ("astro", "astro"),
        ("@angular/core", "angular"),
        ("vite", "vite"),
        ("react", "react"),
        ("react-dom", "react")
    ];

    public FrontendBuildProfile Detect(string rootDirectory, string? angularJson, string? packageJson)
    {
        var normalizedRoot = NormalizeRootDirectory(rootDirectory);
        var framework = DetectFramework(angularJson, packageJson);
        var outputDirectory = TryParseAngularOutputDirectory(angularJson) ??
                              FallbackOutputDirectory(normalizedRoot, framework);
        var buildCommand = TryParseBuildCommand(packageJson) ?? "npm run build";

        return new FrontendBuildProfile(
            normalizedRoot,
            buildCommand,
            "npm install",
            outputDirectory,
            framework);
    }

    internal static string? DetectFramework(string? angularJson, string? packageJson)
    {
        if (!string.IsNullOrWhiteSpace(angularJson) || IsAngularPackage(packageJson))
        {
            return "angular";
        }

        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(packageJson);
            foreach (var (packageName, framework) in FrameworkPackages)
            {
                if (packageName == "@angular/core")
                {
                    continue;
                }

                if (HasDependency(document.RootElement, "dependencies", packageName) ||
                    HasDependency(document.RootElement, "devDependencies", packageName))
                {
                    return framework;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    internal static string? TryParseAngularOutputDirectory(string? angularJson)
    {
        if (string.IsNullOrWhiteSpace(angularJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(angularJson);
            if (!document.RootElement.TryGetProperty("projects", out var projects))
            {
                return null;
            }

            foreach (var project in projects.EnumerateObject())
            {
                if (!project.Value.TryGetProperty("architect", out var architect) ||
                    !architect.TryGetProperty("build", out var build))
                {
                    continue;
                }

                var builder = build.TryGetProperty("builder", out var builderNode)
                    ? builderNode.GetString()
                    : null;
                if (!build.TryGetProperty("options", out var options) ||
                    !options.TryGetProperty("outputPath", out var outputPathNode))
                {
                    continue;
                }

                var outputPath = outputPathNode.GetString();
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    continue;
                }

                outputPath = outputPath.Replace('\\', '/').Trim('/');
                if (builder?.Contains(":application", StringComparison.OrdinalIgnoreCase) == true &&
                    !outputPath.EndsWith("/browser", StringComparison.OrdinalIgnoreCase))
                {
                    outputPath = $"{outputPath}/browser";
                }

                return outputPath;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool IsAngularPackage(string? packageJson)
    {
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(packageJson);
            return HasDependency(document.RootElement, "dependencies", "@angular/core") ||
                   HasDependency(document.RootElement, "devDependencies", "@angular/core");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasDependency(JsonElement root, string section, string packageName)
    {
        return root.TryGetProperty(section, out var dependencies) &&
               dependencies.TryGetProperty(packageName, out _);
    }

    private static string? TryParseBuildCommand(string? packageJson)
    {
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(packageJson);
            if (document.RootElement.TryGetProperty("scripts", out var scripts) &&
                scripts.TryGetProperty("build", out var buildScript))
            {
                var command = buildScript.GetString();
                return string.IsNullOrWhiteSpace(command) ? null : "npm run build";
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string FallbackOutputDirectory(string rootDirectory, string? framework)
    {
        if (framework is "next" or "nuxt" or "sveltekit" or "astro")
        {
            return framework switch
            {
                "next" => ".next",
                "nuxt" => ".output/public",
                "sveltekit" => "build",
                "astro" => "dist",
                _ => "dist"
            };
        }

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return "dist";
        }

        var folderName = rootDirectory.Split('/').Last();
        return string.Equals(folderName, "client", StringComparison.OrdinalIgnoreCase)
            ? "dist/client/browser"
            : $"dist/{folderName}";
    }

    private static string NormalizeRootDirectory(string rootDirectory) =>
        string.IsNullOrWhiteSpace(rootDirectory) ? string.Empty : rootDirectory.Trim().Trim('/');
}
