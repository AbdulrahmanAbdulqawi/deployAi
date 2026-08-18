using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Core.Providers;

/// <summary>
/// Packs a Porkbun API key and secret key into the single opaque
/// <see cref="ProviderCredentials.Token"/> string.
/// </summary>
/// <remarks>
/// Carries an explicit <c>kind</c> discriminator, and <see cref="TryParse"/> refuses anything
/// without it, for the reason <see cref="StorageCredentialStorage"/> documents:
/// <see cref="CoolifyCredentialStorage"/> only sniffs for a leading '{', so an undiscriminated
/// payload would deserialize into an empty-fielded Coolify credential and fail later with a
/// misleading error.
/// </remarks>
public static class PorkbunCredentialStorage
{
    public const string PorkbunKind = "porkbun";

    /// <summary>
    /// Porkbun's prefix for a sandbox key. Both keys carry it, and it is the only way to tell a
    /// test connection from one that spends real money — so it is read rather than asked about.
    /// </summary>
    public const string SandboxPrefix = "pk1_sb_";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(string apiKey, string secretApiKey) =>
        JsonSerializer.Serialize(new StoredPorkbunCredentials
        {
            Kind = PorkbunKind,
            ApiKey = apiKey.Trim(),
            SecretApiKey = secretApiKey.Trim()
        }, JsonOptions);

    public static StoredPorkbunCredentials? TryParse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return null;
        }

        StoredPorkbunCredentials? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StoredPorkbunCredentials>(token, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null || !string.Equals(payload.Kind, PorkbunKind, StringComparison.Ordinal))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(payload.ApiKey) || string.IsNullOrWhiteSpace(payload.SecretApiKey)
            ? null
            : payload;
    }

    /// <summary>Whether these keys address Porkbun's sandbox, where nothing costs real money.</summary>
    public static bool IsSandbox(string? apiKey) =>
        apiKey?.TrimStart().StartsWith(SandboxPrefix, StringComparison.Ordinal) ?? false;

    public sealed class StoredPorkbunCredentials
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("apiKey")]
        public string ApiKey { get; set; } = string.Empty;

        [JsonPropertyName("secretApiKey")]
        public string SecretApiKey { get; set; } = string.Empty;
    }
}
