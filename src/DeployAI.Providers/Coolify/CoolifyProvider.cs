using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Coolify;

public sealed partial class CoolifyProvider : IDeploymentProvider, IProviderManagement
{
    private readonly HttpClient _httpClient;

    public CoolifyProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string ProviderName => ProviderNameValues.Coolify;
    public string DisplayName => "Coolify";
    public string ApiStyle => "rest";

    public async Task<bool> ValidateCredentialsAsync(ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            var session = CoolifyApiSupport.ParseSession(credentials);
            using var healthRequest = CreateRequest(HttpMethod.Get, session, "health");
            var healthResponse = await _httpClient.SendAsync(healthRequest, cancellationToken);
            if (!healthResponse.IsSuccessStatusCode)
            {
                return false;
            }

            using var appsRequest = CreateRequest(HttpMethod.Get, session, "applications");
            var appsResponse = await _httpClient.SendAsync(appsRequest, cancellationToken);
            return appsResponse.IsSuccessStatusCode;
        }
        catch (DeployAIException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ProviderProject>> ListProjectsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        using var request = CreateRequest(HttpMethod.Get, session, "applications");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not list Coolify applications ({(int)response.StatusCode}).");
        }

        var applications = await response.Content.ReadFromJsonAsync<List<CoolifyApplication>>(cancellationToken) ?? [];
        return applications
            .Where(app => !string.IsNullOrWhiteSpace(app.Uuid))
            .Select(app => new ProviderProject(
                app.Uuid!,
                app.Name ?? app.Uuid!,
                NormalizeUrl(app.Fqdn),
                app.GitBranch))
            .ToList();
    }

    public async Task<DeploymentResponse> TriggerDeploymentAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string branch,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var body = new Dictionary<string, string> { ["uuid"] = providerProjectId };
        using var request = CreateRequest(HttpMethod.Post, session, "deploy");
        request.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Coolify deployment failed ({(int)response.StatusCode}).");
        }

        var deployResponse = await response.Content.ReadFromJsonAsync<CoolifyDeployResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Coolify returned an empty deployment response.");

        var deployment = deployResponse.Deployments?.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.DeploymentUuid))
            ?? throw new DeployAIException(
                "coolify_api_error",
                deployResponse.Deployments?.FirstOrDefault()?.Message
                    ?? "Coolify did not return a deployment id.");

        var deployUrl = await TryResolveApplicationUrlAsync(session, providerProjectId, cancellationToken);
        return new DeploymentResponse(deployment.DeploymentUuid!, deployUrl);
    }

    public async Task<DeploymentStatus> GetStatusAsync(
        ProviderCredentials credentials,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var deployment = await GetDeploymentAsync(session, deploymentId, cancellationToken);
        return MapStatus(deployment);
    }

    public async IAsyncEnumerable<string> StreamLogsAsync(
        ProviderCredentials credentials,
        string deploymentId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var seenLength = 0;
        var idleRounds = 0;

        while (!cancellationToken.IsCancellationRequested && idleRounds < 120)
        {
            var deployment = await GetDeploymentAsync(session, deploymentId, cancellationToken);
            var logs = deployment.Logs ?? string.Empty;
            if (logs.Length > seenLength)
            {
                var chunk = logs[seenLength..];
                seenLength = logs.Length;
                idleRounds = 0;

                foreach (var line in chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return line;
                }
            }
            else
            {
                idleRounds++;
            }

            var mapped = MapStatus(deployment);
            if (mapped.Status is DeploymentStatusKind.Success or DeploymentStatusKind.Failed)
            {
                yield break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static DeploymentStatus MapStatus(CoolifyDeployment deployment)
    {
        var status = deployment.Status?.Trim().ToLowerInvariant();
        var deployUrl = NormalizeUrl(deployment.DeploymentUrl);

        return status switch
        {
            "finished" => new DeploymentStatus(DeploymentStatusKind.Success, deployUrl, null),
            "failed" or "cancelled" or "cancelled-by-user" => new DeploymentStatus(
                DeploymentStatusKind.Failed,
                deployUrl,
                deployment.Logs is { Length: > 0 } logs
                    ? logs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
                    : "Publishing did not go through on Coolify."),
            "queued" or "in_progress" or "running" => new DeploymentStatus(
                DeploymentStatusKind.InProgress,
                deployUrl,
                null),
            _ => new DeploymentStatus(DeploymentStatusKind.InProgress, deployUrl, null)
        };
    }

    private async Task<CoolifyDeployment> GetDeploymentAsync(
        CoolifyApiSupport.CoolifySession session,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, $"deployments/{deploymentId}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not read Coolify deployment status ({(int)response.StatusCode}).");
        }

        return await response.Content.ReadFromJsonAsync<CoolifyDeployment>(cancellationToken)
            ?? throw new InvalidOperationException("Coolify returned an empty deployment response.");
    }

    private async Task<string?> TryResolveApplicationUrlAsync(
        CoolifyApiSupport.CoolifySession session,
        string applicationUuid,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, $"applications/{applicationUuid}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var application = await response.Content.ReadFromJsonAsync<CoolifyApplication>(cancellationToken);
        return NormalizeUrl(application?.Fqdn);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        CoolifyApiSupport.CoolifySession session,
        string path)
    {
        var request = new HttpRequestMessage(method, CoolifyApiSupport.BuildApiUri(session, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.ApiToken);
        return request;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"https://{value.TrimStart('/')}";
    }

    private sealed class CoolifyApplication
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("fqdn")]
        public string? Fqdn { get; set; }

        [JsonPropertyName("git_branch")]
        public string? GitBranch { get; set; }
    }

    private sealed class CoolifyDeployResponse
    {
        [JsonPropertyName("deployments")]
        public List<CoolifyDeployEntry>? Deployments { get; set; }
    }

    private sealed class CoolifyDeployEntry
    {
        [JsonPropertyName("deployment_uuid")]
        public string? DeploymentUuid { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class CoolifyDeployment
    {
        [JsonPropertyName("deployment_uuid")]
        public string? DeploymentUuid { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("deployment_url")]
        public string? DeploymentUrl { get; set; }

        [JsonPropertyName("logs")]
        public string? Logs { get; set; }
    }
}
