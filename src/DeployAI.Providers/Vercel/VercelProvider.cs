using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Vercel;

/// <summary>
/// Vercel REST API client. Split into partial classes by concern: this file has the core
/// <see cref="IDeploymentProvider"/> deploy/status/logs operations; <c>VercelProvider.Management</c>
/// has project creation and env vars; <c>VercelProvider.Deploy</c> has git-source/settings
/// resolution for triggering a deploy; <c>VercelProvider.Routing</c> has API-proxy rewrites and
/// public-domain resolution; <c>VercelProvider.Settings</c> has project settings mapping.
/// </summary>
public sealed partial class VercelProvider : IDeploymentProvider, IProviderManagement
{
    private readonly HttpClient _httpClient;

    public VercelProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.vercel.com/");
    }

    public string ProviderName => "vercel";
    public string DisplayName => "Vercel";
    public string ApiStyle => "rest";

    public async Task<bool> ValidateCredentialsAsync(ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "v2/user", credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ProviderProject>> ListProjectsAsync(ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "v9/projects?limit=100", credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<VercelProjectsResponse>(cancellationToken);
        return payload?.Projects?.Select(p => new ProviderProject(p.Id, p.Name, null)).ToList()
               ?? [];
    }

    /// <summary>Applies project settings from <paramref name="environment"/>, re-fetches the project (settings can affect the git source), then triggers a production deployment.</summary>
    public async Task<DeploymentResponse> TriggerDeploymentAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string branch,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var project = await GetProjectAsync(credentials, providerProjectId, cancellationToken);
        await ApplyProjectSettingsAsync(credentials, project.Id, environment, cancellationToken);
        project = await GetProjectAsync(credentials, providerProjectId, cancellationToken);

        var gitSource = BuildGitSource(branch, project.Link, environment);
        var projectSettings = BuildProjectSettings(project, environment);

        var body = new Dictionary<string, object?>
        {
            ["name"] = project.Name,
            ["project"] = project.Id,
            ["gitSource"] = gitSource,
            ["target"] = "production",
            ["projectSettings"] = projectSettings
        };

        var deploymentPath = "v13/deployments?skipAutoDetectionConfirmation=1";
        using var request = CreateRequest(HttpMethod.Post, deploymentPath, credentials.Token);
        request.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "vercel_api_error",
                VercelApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Vercel deployment failed ({(int)response.StatusCode}).");
        }

        var deployment = System.Text.Json.JsonSerializer.Deserialize<VercelDeploymentResponse>(responseBody)
            ?? throw new InvalidOperationException("Vercel returned an empty deployment response.");

        return new DeploymentResponse(deployment.Id, deployment.Url is null ? null : $"https://{deployment.Url}");
    }

    public async Task<DeploymentStatus> GetStatusAsync(ProviderCredentials credentials, string deploymentId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"v13/deployments/{deploymentId}", credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var deployment = await response.Content.ReadFromJsonAsync<VercelDeploymentResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Vercel returned an empty deployment response.");

        return MapStatus(deployment);
    }

    /// <summary>
    /// Polls Vercel's deployment events every 2 seconds and yields new lines, stopping once the
    /// deployment reaches a terminal status or after ~2 minutes (60 rounds) of no new output.
    /// </summary>
    public async IAsyncEnumerable<string> StreamLogsAsync(
        ProviderCredentials credentials,
        string deploymentId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>();
        var idleRounds = 0;

        while (!cancellationToken.IsCancellationRequested && idleRounds < 60)
        {
            using var request = CreateRequest(HttpMethod.Get, $"v2/deployments/{deploymentId}/events", credentials.Token);
            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                yield return "Waiting for activity from Vercel…";
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                idleRounds++;
                continue;
            }

            var events = await response.Content.ReadFromJsonAsync<List<VercelEvent>>(cancellationToken) ?? [];
            var newEvents = 0;
            foreach (var evt in events)
            {
                var line = evt.Text ?? evt.Payload?.Text ?? evt.Type;
                if (string.IsNullOrWhiteSpace(line) || !seen.Add(line))
                {
                    continue;
                }

                newEvents++;
                yield return line;
            }

            using var statusRequest = CreateRequest(HttpMethod.Get, $"v13/deployments/{deploymentId}", credentials.Token);
            var statusResponse = await _httpClient.SendAsync(statusRequest, cancellationToken);
            if (statusResponse.IsSuccessStatusCode)
            {
                var deployment = await statusResponse.Content.ReadFromJsonAsync<VercelDeploymentResponse>(cancellationToken);
                if (deployment is not null)
                {
                    var mapped = MapStatus(deployment);
                    if (mapped.Status is DeploymentStatusKind.Success or DeploymentStatusKind.Failed)
                    {
                        yield break;
                    }
                }
            }

            if (newEvents == 0)
            {
                idleRounds++;
            }
            else
            {
                idleRounds = 0;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static DeploymentStatus MapStatus(VercelDeploymentResponse deployment)
    {
        var state = deployment.ReadyState?.ToUpperInvariant();
        return state switch
        {
            "READY" => new DeploymentStatus(
                DeploymentStatusKind.Success,
                deployment.Url is null ? null : $"https://{deployment.Url}",
                null),
            "ERROR" or "CANCELED" => new DeploymentStatus(
                DeploymentStatusKind.Failed,
                null,
                deployment.ErrorMessage ?? "Publishing did not go through on Vercel."),
            "BUILDING" or "INITIALIZING" or "QUEUED" => new DeploymentStatus(
                DeploymentStatusKind.InProgress,
                null,
                null),
            _ => new DeploymentStatus(DeploymentStatusKind.InProgress, null, null)
        };
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string token)
    {
        // Use absolute URI so query strings (e.g. skipAutoDetectionConfirmation) are never dropped.
        var request = new HttpRequestMessage(method, new Uri(new Uri("https://api.vercel.com/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private sealed class VercelProjectsResponse
    {
        [JsonPropertyName("projects")]
        public List<VercelProject>? Projects { get; set; }
    }

    private sealed class VercelProject
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class VercelDeploymentResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("readyState")]
        public string? ReadyState { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class VercelEvent
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("payload")]
        public VercelEventPayload? Payload { get; set; }
    }

    private sealed class VercelEventPayload
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
