using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Core.Providers;

/// <summary>
/// Packs S3-compatible object-storage credentials into the single opaque
/// <see cref="ProviderCredentials.Token"/> string, the same way
/// <see cref="CoolifyCredentialStorage"/> packs a Coolify instance URL + API token.
///
/// Unlike that one, this payload carries an explicit <c>kind</c> discriminator and
/// <see cref="TryParse"/> refuses anything without it. Coolify's parser only sniffs for a
/// leading '{', so without a discriminator a storage payload would deserialize into an
/// empty-fielded Coolify credential and fail later with a misleading error.
/// </summary>
public static class StorageCredentialStorage
{
    public const string StorageKind = "s3";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(string endpoint, string region, string accessKey, string secretKey) =>
        JsonSerializer.Serialize(new StoredStorageCredentials
        {
            Kind = StorageKind,
            Endpoint = NormalizeEndpoint(endpoint),
            Region = region.Trim(),
            AccessKey = accessKey.Trim(),
            SecretKey = secretKey.Trim()
        }, JsonOptions);

    public static StoredStorageCredentials? TryParse(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return null;
        }

        StoredStorageCredentials? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StoredStorageCredentials>(token, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        // The discriminator is what separates this from a Coolify payload.
        if (payload is null || !string.Equals(payload.Kind, StorageKind, StringComparison.Ordinal))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(payload.Endpoint)
            || string.IsNullOrWhiteSpace(payload.AccessKey)
            || string.IsNullOrWhiteSpace(payload.SecretKey)
            ? null
            : payload;
    }

    public static string NormalizeEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Storage endpoint is required.", nameof(endpoint));
        }

        var trimmed = endpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Storage endpoint must be a valid absolute URL.", nameof(endpoint));
        }

        if (uri.Scheme is not "http" and not "https")
        {
            throw new ArgumentException("Storage endpoint must use http or https.", nameof(endpoint));
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public sealed class StoredStorageCredentials
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("endpoint")]
        public string Endpoint { get; set; } = string.Empty;

        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        [JsonPropertyName("accessKey")]
        public string AccessKey { get; set; } = string.Empty;

        [JsonPropertyName("secretKey")]
        public string SecretKey { get; set; } = string.Empty;
    }
}
