using System.Text.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Coolify;

internal static class CoolifyApiSupport
{
    internal static CoolifySession ParseSession(ProviderCredentials credentials)
    {
        var payload = CoolifyCredentialStorage.TryParse(credentials.Token);
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.InstanceUrl) ||
            string.IsNullOrWhiteSpace(payload.ApiToken))
        {
            throw new DeployAIException(
                "coolify_credentials_invalid",
                "Your Coolify connection is missing the instance URL or API token. Reconnect in settings.");
        }

        return new CoolifySession(payload.InstanceUrl, payload.ApiToken);
    }

    /// <summary>
    /// Coolify's own convention for a domain-free app: <c>{uuid}.{server-ip}.sslip.io</c>, which
    /// resolves to the IP encoded in the hostname with no DNS record needed. Only meaningful when
    /// the instance itself is addressed by a raw IP (every Coolify instance DeployAI has deployed
    /// through so far) — a hostname-addressed instance has no IP to encode, so this returns null
    /// rather than guess.
    /// </summary>
    internal static string? TryBuildSslipDomain(string instanceUrl, string applicationUuid)
    {
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out var uri) ||
            !System.Net.IPAddress.TryParse(uri.Host, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }

        return $"http://{applicationUuid}.{address}.sslip.io";
    }

    internal static string? ParseErrorMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var message = document.RootElement.TryGetProperty("message", out var messageElement) &&
                          messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;

            // Coolify answers a rejected body with a bare "Validation failed." plus a separate
            // `errors` map naming the fields. Without the map the message says nothing at all
            // about what to change.
            if (document.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Object)
            {
                var details = errors
                    .EnumerateObject()
                    .Select(field => $"{field.Name}: {DescribeFieldErrors(field.Value)}")
                    .ToList();

                if (details.Count > 0)
                {
                    return string.IsNullOrWhiteSpace(message)
                        ? string.Join("; ", details)
                        : $"{message} {string.Join("; ", details)}";
                }
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (JsonException)
        {
            // Fall through to raw body.
        }

        return responseBody.Length > 300 ? responseBody[..300] : responseBody;
    }

    private static string DescribeFieldErrors(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return string.Join(", ", value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()));
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    internal static Uri BuildApiUri(CoolifySession session, string path)
    {
        var baseUri = new Uri($"{session.InstanceUrl.TrimEnd('/')}/api/v1/");
        return new Uri(baseUri, path.TrimStart('/'));
    }

    /// <summary>Ensures a directory path has a leading slash, as Coolify's API requires for base_directory/publish_directory.</summary>
    internal static string NormalizeDirectoryPath(string path)
    {
        var trimmed = path.Trim();
        return trimmed.StartsWith('/') ? trimmed : $"/{trimmed}";
    }

    internal static string NormalizeGitHubRepoUrl(string gitHubRepoFullName)
    {
        var trimmed = gitHubRepoFullName.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new DeployAIException(
                "coolify_invalid_repo",
                "GitHub repository must be in owner/repo format.");
        }

        return $"https://github.com/{parts[0]}/{parts[1]}";
    }

    internal static string ResolveBuildPack(CreateProviderProjectRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CoolifyBuildPack) &&
            CoolifyBuildPackValues.TryParse(request.CoolifyBuildPack, out var explicitPack))
        {
            return CoolifyBuildPackValues.ToApiValue(explicitPack);
        }

        // Checked before the Dockerfile branch: a compose app's services have their own
        // Dockerfiles, but compose is what Coolify deploys, so it wins.
        if (!string.IsNullOrWhiteSpace(request.ComposeFileLocation))
        {
            return CoolifyBuildPackValues.DockerCompose;
        }

        if (!string.IsNullOrWhiteSpace(request.DockerfilePath) ||
            string.Equals(request.Framework, "docker", StringComparison.OrdinalIgnoreCase))
        {
            return CoolifyBuildPackValues.Dockerfile;
        }

        // Two independent reasons an output directory does not make something "static", and both
        // must hold for it to be one.
        //
        // Coolify's static build pack does not build. Its generated image is literally
        // `FROM nginx / WORKDIR /usr/share/nginx/html / COPY . .` — the repository is copied in
        // as-is. Anything with a build command must go through Nixpacks, which installs and builds
        // and then serves the output; sending Angular here publishes package.json and src/.
        //
        // And an SSR framework (Next, Nuxt, SvelteKit, Remix) has an output dir (.next, .output,
        // build) but ships a Node server, so serving that output as static files gets a blank page
        // and no server-rendered routes.
        if (!string.IsNullOrWhiteSpace(request.OutputDirectory) &&
            string.IsNullOrWhiteSpace(request.BuildCommand) &&
            !IsServerRenderedFrontend(request.Framework))
        {
            return CoolifyBuildPackValues.Static;
        }

        return CoolifyBuildPackValues.Nixpacks;
    }

    /// <summary>
    /// Field-based form, for a config sync that holds individual settings rather than a create
    /// request. There is no compose branch here: compose location is not part of an update.
    /// </summary>
    internal static string ResolveBuildPack(
        string? coolifyBuildPack,
        string? dockerfilePath,
        string? framework,
        string? outputDirectory,
        string? buildCommand,
        string? composeFileLocation = null)
    {
        if (!string.IsNullOrWhiteSpace(coolifyBuildPack) &&
            CoolifyBuildPackValues.TryParse(coolifyBuildPack, out var explicitPack))
        {
            return CoolifyBuildPackValues.ToApiValue(explicitPack);
        }

        // Same precedence as the create path above, and for the same reason: a compose app's
        // services have their own Dockerfiles and its website half has a framework and a build
        // command, so every rule below would answer confidently and wrongly. This overload had no
        // compose parameter at all, which is why a compose application was re-described as a plain
        // Angular site on the config sync before every deploy — Nixpacks, `npm run build` at the
        // repo root, and the API and database in the compose file never built.
        if (!string.IsNullOrWhiteSpace(composeFileLocation))
        {
            return CoolifyBuildPackValues.DockerCompose;
        }

        if (!string.IsNullOrWhiteSpace(dockerfilePath) ||
            string.Equals(framework, "docker", StringComparison.OrdinalIgnoreCase))
        {
            return CoolifyBuildPackValues.Dockerfile;
        }

        // Two independent reasons an output directory does not imply "static", and both must hold
        // for it to be one. The static build pack skips the build step and copies the tree as-is,
        // so anything with a build command needs nixpacks to compile first. And an SSR framework
        // ships a Node server, so serving its output as static files gets a blank page.
        if (!string.IsNullOrWhiteSpace(outputDirectory) &&
            string.IsNullOrWhiteSpace(buildCommand) &&
            !IsServerRenderedFrontend(framework))
        {
            return CoolifyBuildPackValues.Static;
        }

        return CoolifyBuildPackValues.Nixpacks;
    }

    /// <summary>
    /// A frontend that ships its own server, so serving its build output as static files gets a
    /// blank page.
    /// </summary>
    /// <remarks>
    /// This kept its own list — next, nextjs, nuxt, sveltekit, remix — while
    /// <see cref="DeployAI.Core.Deployments.SsrFrontendFrameworks"/> held a longer one that also
    /// covers Angular, Vite, React, Vue, Svelte and Astro. Two lists answering one question drift,
    /// and this pair already had: an Angular app was "server-rendered" to the half of the code that
    /// generates a Dockerfile for it and "not server-rendered" to the half that picks a build pack.
    /// It never surfaced only because the static branch also requires an empty build command, and an
    /// Angular app always has one. Deferring to the shared list removes the second answer rather
    /// than waiting for a repository that exposes the difference.
    /// </remarks>
    private static bool IsServerRenderedFrontend(string? framework) =>
        DeployAI.Core.Deployments.SsrFrontendFrameworks.Inlines(framework);

    /// <summary>
    /// Deleting a Coolify resource leaves its volumes, generated configuration and networks behind
    /// unless it is asked to remove them. For a database that means the data volume — and the disk
    /// it occupies — survives the delete, so an app removed from DeployAI keeps costing storage
    /// with nothing referencing it. Sent explicitly rather than trusting the API's defaults, which
    /// differ between Coolify versions.
    /// </summary>
    internal const string ResourceCleanupQuery =
        "?delete_configurations=true&delete_volumes=true&docker_cleanup=true&delete_connected_networks=true";

    internal static bool IsComposeBuildPack(string buildPack) =>
        string.Equals(buildPack, CoolifyBuildPackValues.DockerCompose, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc cref="DeployAI.Core.Deployments.SsrFrontendFrameworks.Inlines" />
    internal static bool InlinesBuildTimeEnvironment(string? framework) =>
        DeployAI.Core.Deployments.SsrFrontendFrameworks.Inlines(framework);

    /// <summary>
    /// The container port Coolify's proxy routes to. Null for compose, where the compose file
    /// declares its own ports and a single number is meaningless.
    /// </summary>
    internal static string? ResolveExposedPort(string buildPack, CreateProviderProjectRequest request) =>
        ResolveExposedPort(buildPack, request.ExposedPort, request.Framework);

    /// <summary>
    /// Field-based form, for callers that hold a config-update request rather than a create
    /// request. Both go through the same rules so a later config sync cannot resolve a different
    /// port than the one the application was created with.
    /// </summary>
    internal static string? ResolveExposedPort(string buildPack, string? exposedPort, string? framework)
    {
        if (IsComposeBuildPack(buildPack))
        {
            return null;
        }

        // An explicit port (e.g. from the Dockerfile's EXPOSE that we generated) wins over guessing
        // — the framework is often just "docker" for a Dockerfile build, which the switch below
        // can't map to a real port.
        if (!string.IsNullOrWhiteSpace(exposedPort))
        {
            return exposedPort.Trim();
        }

        if (string.Equals(buildPack, CoolifyBuildPackValues.Static, StringComparison.OrdinalIgnoreCase))
        {
            return "80";
        }

        // .NET containers listen on 8080 by default. The previous blanket "3000" silently
        // pointed the proxy at a port nothing was listening on.
        var frameworkPort = framework?.Trim().ToLowerInvariant() switch
        {
            "dotnet" or "aspnet" or "aspnetcore" => "8080",
            "python" or "django" or "flask" or "fastapi" => "8000",
            "go" or "rust" => "8080",
            _ => null
        };

        if (frameworkPort is not null)
        {
            return frameworkPort;
        }

        // A Dockerfile build's framework is usually just "docker", or absent entirely, so the map
        // above cannot resolve it — and falling through to 3000 points the proxy at a port nothing
        // is listening on. .NET 8+ images are the dominant Dockerfile shape here and listen on
        // 8080. An explicit port, read from the EXPOSE line we generate, still wins above this.
        return string.Equals(buildPack, CoolifyBuildPackValues.Dockerfile, StringComparison.OrdinalIgnoreCase)
            ? "8080"
            : "3000";
    }

    internal sealed record CoolifySession(string InstanceUrl, string ApiToken);
}
