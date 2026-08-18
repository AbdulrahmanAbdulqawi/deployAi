using System.Net;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Vercel;

public sealed partial class VercelProvider : IProviderApplicationExistence
{
    /// <summary>
    /// Asks Vercel whether a project still exists, keeping "deleted" apart from "we could not look".
    /// </summary>
    /// <remarks>
    /// Vercel rate-limits, and a 429 is the case that makes the distinction earn its keep: reporting
    /// a throttled request as a deleted project would tell a user their site was gone at exactly the
    /// moment DeployAI was asking too often.
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
                "This app has no Vercel project recorded against it yet.");
        }

        HttpResponseMessage response;
        try
        {
            using var request = CreateRequest(
                HttpMethod.Get, $"v9/projects/{providerProjectId}", credentials.Token);
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Unknown, null, null,
                $"DeployAI could not reach Vercel to check this app ({ex.GetType().Name}).");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Absent, null, null,
                "The project this app deploys to no longer exists on Vercel.");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Unknown, null, null,
                "This Vercel connection is no longer authorised, so DeployAI could not check this app.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Unknown, null, null,
                $"Vercel answered {(int)response.StatusCode} when asked about this app.");
        }

        return new ProviderApplicationExistence(
            ProviderApplicationPresence.Present, null, null,
            "The project exists on Vercel.");
    }
}
