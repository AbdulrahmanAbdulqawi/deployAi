using System.Text.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Coolify;

/// <summary>Shared request-building, parsing, and build-config-resolution helpers used across the <see cref="CoolifyProvider"/> partial classes.</summary>
internal static class CoolifyApiSupport
{
    /// <summary>Decodes a credential's packed token into a Coolify session, throwing if it's missing or malformed.</summary>
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

    /// <summary>Extracts a human-readable error message from a Coolify error response (message + per-field validation errors), falling back to the raw body.</summary>
    internal static string? ParseErrorMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var message = document.RootElement.TryGetProperty("message", out var messageProperty) &&
                messageProperty.ValueKind == JsonValueKind.String
                ? messageProperty.GetString()
                : null;

            if (document.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Object)
            {
                var fieldMessages = errors.EnumerateObject()
                    .SelectMany(field => field.Value.ValueKind == JsonValueKind.Array
                        ? field.Value.EnumerateArray().Select(v => $"{field.Name}: {v.GetString()}")
                        : [$"{field.Name}: {field.Value}"]);
                var detail = string.Join(" ", fieldMessages);
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    return string.IsNullOrWhiteSpace(message) ? detail : $"{message} {detail}";
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

    /// <summary>Builds the full API URI for a path against a Coolify instance.</summary>
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

    /// <summary>Normalizes an "owner/repo" string (or an already-full URL) into the full GitHub HTTPS URL Coolify expects.</summary>
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

    /// <summary>Resolves the Coolify build_pack to use for a new application, from a create-project request.</summary>
    internal static string ResolveBuildPack(CreateProviderProjectRequest request) =>
        ResolveBuildPack(
            request.CoolifyBuildPack,
            request.DockerfilePath,
            request.Framework,
            request.OutputDirectory,
            request.BuildCommand);

    /// <summary>
    /// Resolves the Coolify build_pack from individual fields: an explicit override wins, then a
    /// Dockerfile/docker framework, then falls back to static (no build step) vs. nixpacks
    /// (compiles first) based on whether a build command is present.
    /// </summary>
    internal static string ResolveBuildPack(
        string? coolifyBuildPack,
        string? dockerfilePath,
        string? framework,
        string? outputDirectory,
        string? buildCommand)
    {
        if (!string.IsNullOrWhiteSpace(coolifyBuildPack) &&
            CoolifyBuildPackValues.TryParse(coolifyBuildPack, out var explicitPack))
        {
            return CoolifyBuildPackValues.ToApiValue(explicitPack);
        }

        if (!string.IsNullOrWhiteSpace(dockerfilePath) ||
            string.Equals(framework, "docker", StringComparison.OrdinalIgnoreCase))
        {
            return CoolifyBuildPackValues.Dockerfile;
        }

        // "static" build pack skips the build step and copies the source tree as-is - only
        // correct when there's nothing to compile. Anything with a build command still needs
        // nixpacks (which runs install/build, then serves OutputDirectory via nginx).
        if (!string.IsNullOrWhiteSpace(outputDirectory) &&
            string.IsNullOrWhiteSpace(buildCommand))
        {
            return CoolifyBuildPackValues.Static;
        }

        return CoolifyBuildPackValues.Nixpacks;
    }

    /// <summary>Resolves the port Coolify should expose for the given build pack (80 for static, 8080 for Dockerfile/.NET, 3000 otherwise).</summary>
    internal static string ResolveExposedPort(string buildPack)
    {
        if (string.Equals(buildPack, CoolifyBuildPackValues.Static, StringComparison.OrdinalIgnoreCase))
        {
            return "80";
        }

        // .NET 8+ container images default to ASPNETCORE_HTTP_PORTS=8080; Dockerfile-based
        // deploys are most commonly .NET in this codebase, so that's the safer default. There's
        // no way to know the real port without parsing the Dockerfile's EXPOSE directive.
        return string.Equals(buildPack, CoolifyBuildPackValues.Dockerfile, StringComparison.OrdinalIgnoreCase)
            ? "8080"
            : "3000";
    }

    internal sealed record CoolifySession(string InstanceUrl, string ApiToken);
}
