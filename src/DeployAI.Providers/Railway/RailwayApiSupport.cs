using System.Text.Json;
using DeployAI.Core.Exceptions;
using StrawberryShake;

namespace DeployAI.Providers.Railway;

internal static class RailwayApiSupport
{
    public static T EnsureData<T>(IOperationResult<T> result)
        where T : class
    {
        EnsureSuccess(result);
        return result.Data!;
    }

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
}
