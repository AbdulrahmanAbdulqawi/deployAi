using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Core.Providers;

public static class CoolifyCredentialStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool IsCoolifyPayload(string token) =>
        token.TrimStart().StartsWith("{", StringComparison.Ordinal);

    public static string Serialize(string instanceUrl, string apiToken) =>
        JsonSerializer.Serialize(new StoredCoolifyCredentials
        {
            InstanceUrl = NormalizeInstanceUrl(instanceUrl),
            ApiToken = apiToken.Trim()
        }, JsonOptions);

    public static StoredCoolifyCredentials? TryParse(string token)
    {
        if (!IsCoolifyPayload(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredCoolifyCredentials>(token, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string NormalizeInstanceUrl(string instanceUrl)
    {
        if (string.IsNullOrWhiteSpace(instanceUrl))
        {
            throw new ArgumentException("Coolify instance URL is required.", nameof(instanceUrl));
        }

        var trimmed = instanceUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Coolify instance URL must be a valid absolute URL.", nameof(instanceUrl));
        }

        if (uri.Scheme is not "http" and not "https")
        {
            throw new ArgumentException("Coolify instance URL must use http or https.", nameof(instanceUrl));
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public sealed class StoredCoolifyCredentials
    {
        [JsonPropertyName("instanceUrl")]
        public string InstanceUrl { get; set; } = string.Empty;

        [JsonPropertyName("apiToken")]
        public string ApiToken { get; set; } = string.Empty;
    }
}
