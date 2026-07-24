using System.Text.Json;
using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;

namespace DeployAI.Infrastructure.Adapters;

/// <summary>
/// Node/Express as a plugin — the proof that the backend layer is genuinely pluggable: a
/// different base image, entrypoint, port, health convention, and env style than .NET, behind
/// the same interface, with the shapes and generators untouched.
/// </summary>
public sealed class NodeExpressAdapter : IFrameworkAdapter
{
    public string Id => "node-express";

    public string? HealthPath => "/health";

    public EnvKeyConvention EnvConvention => EnvKeyConvention.FlatUpperSnake;

    /// <summary>Node's conventional listen port.</summary>
    public const int ContainerPort = 3000;

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

            // A frontend framework in the same package.json means this is not a Node backend —
            // that claim belongs to the frontend adapter, which scores higher anyway.
            if (HasAnyDependency(root, "next", "nuxt", "@sveltejs/kit", "astro", "@angular/core", "vite"))
            {
                return null;
            }

            var hasExpress = HasAnyDependency(root, "express", "fastify", "koa", "@nestjs/core");
            var hasStart = TryGetScript(root, "start") is not null;

            if (!hasExpress && !hasStart)
            {
                return null;
            }

            // A server dependency is a strong signal; a bare start script is weak — deliberately
            // below the frontend adapters' 0.9+ so they win contested package.json files.
            return new AdapterDetection(
                hasExpress ? 0.8 : 0.4,
                ServiceCapability.HttpService);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ServiceNode CreateServiceNode(string serviceId, RepositorySignals signals)
    {
        var entry = ResolveEntryPoint(signals.PackageJson);
        var hasBuild = TryGetScript(ParseOrNull(signals.PackageJson), "build") is not null;

        DockerfileSpec dockerfile;
        if (signals.HasDockerfile)
        {
            dockerfile = new DockerfileSpec(null, ExistingPath: "Dockerfile", ExposedPort: ContainerPort);
        }
        else
        {
            var buildStep = hasBuild ? "RUN npm run build\n" : string.Empty;
            var content = $"""
                # Built with ./{signals.Directory} as the context; COPY paths are relative to it.
                FROM node:22-alpine
                WORKDIR /app
                COPY package*.json ./
                RUN npm ci --omit=dev
                COPY . .
                {buildStep}ENV NODE_ENV=production
                ENV PORT={ContainerPort}
                EXPOSE {ContainerPort}
                CMD ["node", "{entry}"]
                """;

            dockerfile = new DockerfileSpec(content, ExistingPath: null, ExposedPort: ContainerPort);
        }

        return new ServiceNode(
            serviceId,
            ServiceCapability.HttpService,
            ServiceSource.FromBuild(signals.Directory, dockerfile),
            Framework: Id,
            Ports: [ContainerPort],
            HealthPath: HealthPath);
    }

    /// <summary>package.json "main", else the file a plain `node x.js` start script names, else index.js.</summary>
    public static string ResolveEntryPoint(string? packageJson)
    {
        var root = ParseOrNull(packageJson);
        if (root is { } element)
        {
            if (element.TryGetProperty("main", out var main) &&
                main.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(main.GetString()))
            {
                return main.GetString()!;
            }

            var start = TryGetScript(element, "start");
            if (start is not null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(start, @"node\s+([\w./-]+\.[mc]?js)");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }

        return "index.js";
    }

    private static JsonElement? ParseOrNull(string? packageJson)
    {
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(packageJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetScript(JsonElement? root, string name)
    {
        if (root is not { } element ||
            !element.TryGetProperty("scripts", out var scripts) ||
            scripts.ValueKind != JsonValueKind.Object ||
            !scripts.TryGetProperty(name, out var script) ||
            script.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return script.GetString();
    }

    private static bool HasAnyDependency(JsonElement root, params string[] names)
    {
        foreach (var section in new[] { "dependencies", "devDependencies" })
        {
            if (!root.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var name in names)
            {
                if (deps.TryGetProperty(name, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
