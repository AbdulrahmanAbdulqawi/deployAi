using System.Net;
using System.Text.Json;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Coolify;

public sealed partial class CoolifyProvider : IProviderApplicationExistence
{
    /// <summary>
    /// Asks Coolify whether an application is still there, keeping "deleted" apart from "we could
    /// not look".
    /// </summary>
    /// <remarks>
    /// Deliberately does not reuse <c>TryGetApplicationAsync</c>, which returns null for every
    /// non-2xx and would collapse the two answers this method exists to separate. The status code is
    /// the whole signal: 404 is Coolify saying the application does not exist, and anything else is
    /// Coolify not answering the question.
    /// </remarks>
    public async Task<ProviderApplicationExistence> CheckApplicationExistsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerProjectId))
        {
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Unknown, null, null,
                "This app has no Coolify application recorded against it yet.");
        }

        HttpResponseMessage response;
        string body;
        try
        {
            var session = CoolifyApiSupport.ParseSession(credentials);
            using var request = CreateRequest(HttpMethod.Get, session, $"applications/{providerProjectId}");
            response = await _httpClient.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A connection that never opened proves nothing about the application. Reporting this as
            // absent would tell a user their app had been deleted every time their network hiccuped.
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Unknown, null, null,
                $"DeployAI could not reach Coolify to check this app ({ex.GetType().Name}).");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Absent, null, null,
                "The application this app deploys to no longer exists on Coolify.");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Unknown, null, null,
                "This Coolify connection is no longer authorised, so DeployAI could not check this app.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Unknown, null, null,
                $"Coolify answered {(int)response.StatusCode} when asked about this app.");
        }

        var (state, fqdn) = ReadApplication(body);
        return new ProviderApplicationExistence(
            ProviderApplicationPresence.Present,
            state,
            NormalizeUrl(fqdn),
            state is null
                ? "The application exists on Coolify."
                : $"The application exists on Coolify and reports \"{state}\".");
    }

    /// <summary>
    /// Reads the state and address out of the application payload, tolerating a body that does not
    /// parse — a 200 is already proof of existence, so a shape surprise must not downgrade the
    /// answer to "unknown".
    /// </summary>
    private static (string? State, string? Fqdn) ReadApplication(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var state = document.RootElement.TryGetProperty("status", out var status) &&
                        status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;

            var fqdn = document.RootElement.TryGetProperty("fqdn", out var address) &&
                       address.ValueKind == JsonValueKind.String
                ? address.GetString()
                : null;

            return (state, fqdn);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
