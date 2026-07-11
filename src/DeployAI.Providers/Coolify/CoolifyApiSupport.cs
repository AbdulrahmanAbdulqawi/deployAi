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
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // Fall through to raw body.
        }

        return responseBody.Length > 300 ? responseBody[..300] : responseBody;
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

    internal static string ResolveExposedPort(string buildPack) =>
        string.Equals(buildPack, CoolifyBuildPackValues.Static, StringComparison.OrdinalIgnoreCase)
            ? "80"
            : "3000";

    internal sealed record CoolifySession(string InstanceUrl, string ApiToken);
}
