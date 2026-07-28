using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Core.Providers;

/// <summary>
/// Coolify needs both an instance URL and an API token to make any request, unlike other providers
/// which store a single opaque token - this packs both into the single string
/// <see cref="ProviderCredentials.Token"/> field (as JSON) so Coolify fits the same storage shape
/// as every other provider's credential.
/// </summary>
public static class CoolifyCredentialStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Checks whether a stored token string is a serialized Coolify credentials payload (vs. a plain opaque token).</summary>
    public static bool IsCoolifyPayload(string token) =>
        token.TrimStart().StartsWith("{", StringComparison.Ordinal);

    /// <summary>Serializes an instance URL and API token into the packed string stored as a credential's token.</summary>
    public static string Serialize(string instanceUrl, string apiToken) =>
        JsonSerializer.Serialize(new StoredCoolifyCredentials
        {
            InstanceUrl = NormalizeInstanceUrl(instanceUrl),
            ApiToken = apiToken.Trim()
        }, JsonOptions);

    /// <summary>Parses a stored token string back into instance URL + API token, or null if it isn't a valid Coolify payload.</summary>
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

    /// <summary>Validates and normalizes a Coolify instance URL to its scheme+host+port, trimming any path.</summary>
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

    /// <summary>A Coolify connection's instance URL and API token, as decoded from a stored credential.</summary>
    public sealed class StoredCoolifyCredentials
    {
        [JsonPropertyName("instanceUrl")]
        public string InstanceUrl { get; set; } = string.Empty;

        [JsonPropertyName("apiToken")]
        public string ApiToken { get; set; } = string.Empty;
    }
}
