using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Exceptions;

namespace DeployAI.Providers.Railway;

internal static class RailwayApiSupport
{
    public static async Task<JsonDocument> ExecuteAsync(
        HttpClient httpClient,
        string token,
        string query,
        object? variables,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://backboard.railway.com/graphql/v2");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query, variables }),
            Encoding.UTF8,
            "application/json");

        var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "railway_api_error",
                ParseErrorMessage(body) ?? "Railway did not respond. Try again in a moment.");
        }

        var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            throw new DeployAIException("railway_api_error", message ?? "Railway returned an error.");
        }

        return document;
    }

    public static async Task<JsonDocument?> TryExecuteAsync(
        HttpClient httpClient,
        string token,
        string query,
        object? variables,
        Func<string?, bool> ignoreError,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://backboard.railway.com/graphql/v2");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query, variables }),
            Encoding.UTF8,
            "application/json");

        var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var message = ParseErrorMessage(body);
            if (ignoreError(message))
            {
                return null;
            }

            throw new DeployAIException(
                "railway_api_error",
                message ?? "Railway did not respond. Try again in a moment.");
        }

        var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            if (ignoreError(message))
            {
                document.Dispose();
                return null;
            }

            throw new DeployAIException("railway_api_error", message ?? "Railway returned an error.");
        }

        return document;
    }

    public static bool IsBuildNotReadyError(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        message.Contains("associated build", StringComparison.OrdinalIgnoreCase);

    public static bool IsAuthorizationError(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        message.Contains("not authorized", StringComparison.OrdinalIgnoreCase);

    public static bool IsDuplicateServiceNameError(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        message.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    public static bool IsDuplicateVolumeError(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        message.Contains("volume", StringComparison.OrdinalIgnoreCase) &&
        message.Contains("already", StringComparison.OrdinalIgnoreCase);

    public static string? ParseErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array &&
                errors.GetArrayLength() > 0 &&
                errors[0].TryGetProperty("message", out var message))
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // Ignore malformed bodies.
        }

        return null;
    }

    public static string BuildProviderProjectId(string serviceId, string environmentId) =>
        $"{serviceId}|{environmentId}";

    public static (string ServiceId, string EnvironmentId) ParseProviderProjectId(string providerProjectId)
    {
        var parts = providerProjectId.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new DeployAIException(
                "invalid_railway_target",
                "This Railway connection is missing setup details. Reconnect the service in settings.");
        }

        return (parts[0], parts[1]);
    }

    public static string NormalizeGitHubRepo(string repoFullName) =>
        repoFullName.Trim().Replace("https://github.com/", string.Empty, StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeSecret(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalizedName = name.ToUpperInvariant();
        return normalizedName.Contains("SECRET", StringComparison.Ordinal) ||
               normalizedName.Contains("PASSWORD", StringComparison.Ordinal) ||
               normalizedName.Contains("TOKEN", StringComparison.Ordinal) ||
               normalizedName.Contains("KEY", StringComparison.Ordinal) ||
               value.Length >= 32;
    }

    internal sealed class GraphQlData<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }
}
