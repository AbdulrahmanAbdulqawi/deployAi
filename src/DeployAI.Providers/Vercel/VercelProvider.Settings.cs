using System.Net.Http.Json;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Vercel;

/// <summary>PATCHes Vercel project settings (build/root directory config) from various input shapes - a raw dictionary, a create-project request, or a target's environment entries.</summary>
public sealed partial class VercelProvider
{
    private async Task ApplyProjectSettingsAsync(
        ProviderCredentials credentials,
        string projectId,
        Dictionary<string, object?> settings,
        CancellationToken cancellationToken)
    {
        if (settings.Count == 0)
        {
            return;
        }

        using var httpRequest = CreateRequest(
            HttpMethod.Patch,
            $"v9/projects/{Uri.EscapeDataString(projectId)}",
            credentials.Token);
        httpRequest.Content = JsonContent.Create(settings);
        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task ApplyProjectSettingsAsync(
        ProviderCredentials credentials,
        string projectId,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        var settings = VercelApiSupport.BuildSettingsFromCreateRequest(request);
        await ApplyProjectSettingsAsync(credentials, projectId, settings, cancellationToken);
    }

    private async Task ApplyProjectSettingsAsync(
        ProviderCredentials credentials,
        string projectId,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var settings = VercelApiSupport.BuildSettingsFromEnvironment(environment);
        await ApplyProjectSettingsAsync(credentials, projectId, settings, cancellationToken);
    }
}
