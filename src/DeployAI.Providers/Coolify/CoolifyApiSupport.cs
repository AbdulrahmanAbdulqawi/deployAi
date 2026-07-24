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

        if (!string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            return CoolifyBuildPackValues.Static;
        }

        return CoolifyBuildPackValues.Nixpacks;
    }

    internal static bool IsComposeBuildPack(string buildPack) =>
        string.Equals(buildPack, CoolifyBuildPackValues.DockerCompose, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The container port Coolify's proxy routes to. Null for compose, where the compose file
    /// declares its own ports and a single number is meaningless.
    /// </summary>
    internal static string? ResolveExposedPort(string buildPack, CreateProviderProjectRequest request)
    {
        if (IsComposeBuildPack(buildPack))
        {
            return null;
        }

        if (string.Equals(buildPack, CoolifyBuildPackValues.Static, StringComparison.OrdinalIgnoreCase))
        {
            return "80";
        }

        // .NET containers listen on 8080 by default. The previous blanket "3000" silently
        // pointed the proxy at a port nothing was listening on.
        return request.Framework?.Trim().ToLowerInvariant() switch
        {
            "dotnet" or "aspnet" or "aspnetcore" => "8080",
            "python" or "django" or "flask" or "fastapi" => "8000",
            "go" or "rust" => "8080",
            _ => "3000"
        };
    }

    internal sealed record CoolifySession(string InstanceUrl, string ApiToken);
}
