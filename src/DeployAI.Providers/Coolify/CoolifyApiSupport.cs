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

    internal sealed record CoolifySession(string InstanceUrl, string ApiToken);
}
