using System.Text.Json;
using DeployAI.Core.Exceptions;
using StrawberryShake;

namespace DeployAI.Providers.Railway;

/// <summary>
/// Shared GraphQL result handling, id encoding, and misc helpers used across the
/// <see cref="RailwayProvider"/> partial classes. Railway's <c>providerProjectId</c> is a composite
/// "serviceId|environmentId" string (see <see cref="BuildProviderProjectId"/>/<see cref="ParseProviderProjectId"/>)
/// since a Railway service alone doesn't identify which environment to act on.
/// </summary>
internal static class RailwayApiSupport
{
    /// <summary>Unwraps a GraphQL result, throwing a <see cref="DeployAIException"/> if it has errors or no data.</summary>
    public static T EnsureData<T>(IOperationResult<T> result)
        where T : class
    {
        EnsureSuccess(result);
        return result.Data!;
    }

    /// <summary>Throws a <see cref="DeployAIException"/> if a GraphQL result has errors or no data, without returning the data.</summary>
    public static void EnsureSuccess<T>(IOperationResult<T> result)
        where T : class
    {
        if (result.Errors.Count > 0)
        {
            var message = result.Errors[0].Message;
            throw new DeployAIException("railway_api_error", message ?? "Railway returned an error.");
        }

        if (result.Data is null)
        {
            throw new DeployAIException("railway_api_error", "Railway returned an empty response.");
        }
    }

    /// <summary>Unwraps a GraphQL result, returning null instead of throwing when the error matches <paramref name="ignoreError"/> (for expected/recoverable error conditions).</summary>
    public static T? TryGetData<T>(IOperationResult<T> result, Func<string?, bool> ignoreError)
        where T : class
    {
        if (result.Errors.Count > 0)
        {
            var message = result.Errors[0].Message;
            if (ignoreError(message))
            {
                return null;
            }

            throw new DeployAIException("railway_api_error", message ?? "Railway returned an error.");
        }

        return result.Data;
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

    /// <summary>Encodes a service+environment pair into the composite id DeployAI stores as a Railway deploy target's providerProjectId.</summary>
    public static string BuildProviderProjectId(string serviceId, string environmentId) =>
        $"{serviceId}|{environmentId}";

    /// <summary>Decodes a composite providerProjectId back into its service id and environment id.</summary>
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

    /// <summary>Heuristically detects whether an env var looks like a secret (name contains SECRET/PASSWORD/TOKEN/KEY, or a long value), so its value can be hidden when listing.</summary>
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
}
