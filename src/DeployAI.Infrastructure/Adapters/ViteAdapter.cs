using System.Text.Json;
using System.Text.RegularExpressions;
using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;

namespace DeployAI.Infrastructure.Adapters;

/// <summary>
/// A Vite-built bundle — React, Vue, Svelte, Preact, Solid — as a plugin.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a non-Angular front end deployable as a compose service. Without it, a Vite
/// repository's <c>package.json</c> reached only <see cref="NodeExpressAdapter"/>, which either
/// declined it or claimed it as an <c>HttpService</c> — and a graph with two HTTP services and no
/// static site is one the single-origin transform refuses, so the deployment had no site to front
/// it with.
/// </para>
/// <para>
/// A framework that ships its own server (Next, Nuxt, SvelteKit, Astro) is deliberately not
/// claimed. Those are not static bundles: serving their build output through nginx drops the
/// server half of the app, and the failure is a site that renders and 404s its own routes.
/// </para>
/// </remarks>
public sealed class ViteAdapter : IFrameworkAdapter
{
    public string Id => "vite";

    /// <summary>A built bundle has no server of its own; health is the proxy's concern.</summary>
    public string? HealthPath => null;

    /// <summary>Vite exposes only <c>VITE_*</c> variables to the bundle, and only at build time.</summary>
    public EnvKeyConvention EnvConvention => EnvKeyConvention.FlatUpperSnake;

    /// <summary>Vite's own default, used when the config does not say otherwise.</summary>
    internal const string DefaultOutputDirectory = "dist";

    /// <summary>Frameworks that ship a server; their build output is not a static bundle.</summary>
    private static readonly string[] ServerRendered = ["next", "nuxt", "@sveltejs/kit", "astro", "@remix-run/node"];

    public AdapterDetection? Detect(RepositorySignals signals)
    {
        if (string.IsNullOrWhiteSpace(signals.PackageJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(signals.PackageJson);
            var root = document.RootElement;

            // Angular has its own adapter and its own build layout; a package.json carrying both
            // is Angular's, not this one's.
            if (HasAnyDependency(root, "@angular/core") || HasAnyDependency(root, ServerRendered))
            {
                return null;
            }

            if (!HasAnyDependency(root, "vite"))
            {
                return null;
            }

            // A vite.config is as unambiguous as angular.json; the dependency alone is nearly so.
            // Either way this must outrank the generic Node adapter reading the same file.
            return new AdapterDetection(
                signals.ViteConfig is not null ? 0.95 : 0.9,
                ServiceCapability.StaticSite);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ServiceNode CreateServiceNode(string serviceId, RepositorySignals signals)
    {
        var outputDirectory = ReadOutputDirectory(signals.ViteConfig) ?? DefaultOutputDirectory;
        var buildCommand = ReadBuildScript(signals.PackageJson) is null ? "npx vite build" : "npm run build";

        return new ServiceNode(
            serviceId,
            ServiceCapability.StaticSite,
            ServiceSource.FromBuild(
                signals.Directory,
                new DockerfileSpec(
                    StaticBundleDockerfile.Render(signals.Directory, buildCommand, outputDirectory),
                    ExistingPath: null,
                    ExposedPort: StaticBundleDockerfile.ExposedPort)),
            Framework: Id,
            Ports: [StaticBundleDockerfile.ExposedPort]);
    }

    /// <summary>
    /// <c>build.outDir</c> from a vite.config. Matched textually rather than by evaluating the
    /// config, which is a TypeScript module and cannot be run here — a config that computes its
    /// output directory falls through to Vite's default, and a wrong guess fails at the
    /// Dockerfile's COPY rather than publishing an empty site.
    /// </summary>
    internal static string? ReadOutputDirectory(string? viteConfig)
    {
        if (string.IsNullOrWhiteSpace(viteConfig))
        {
            return null;
        }

        var match = Regex.Match(viteConfig, @"outDir\s*:\s*['""]([^'""]+)['""]");
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value.Trim().Trim('/');
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? ReadBuildScript(string? packageJson)
    {
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(packageJson);
            return document.RootElement.TryGetProperty("scripts", out var scripts) &&
                   scripts.ValueKind == JsonValueKind.Object &&
                   scripts.TryGetProperty("build", out var build) &&
                   build.ValueKind == JsonValueKind.String
                ? build.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasAnyDependency(JsonElement root, params string[] names)
    {
        foreach (var section in new[] { "dependencies", "devDependencies" })
        {
            if (!root.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (names.Any(name => deps.TryGetProperty(name, out _)))
            {
                return true;
            }
        }

        return false;
    }
}
