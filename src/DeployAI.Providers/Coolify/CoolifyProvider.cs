using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeployAI.Providers.Coolify;

public sealed partial class CoolifyProvider : IDeploymentProvider, IProviderManagement
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CoolifyProvider> _logger;

    public CoolifyProvider(HttpClient httpClient, ILogger<CoolifyProvider>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger ?? NullLogger<CoolifyProvider>.Instance;
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

        // Frontend frameworks bake env vars into the bundle at build time (Next.js inlines
        // NEXT_PUBLIC_*, Vite import.meta.env, Angular its environment files). Coolify reuses its
        // build cache by default, so a freshly-changed API URL is silently ignored and the app
        // keeps calling the old one. Force a cache-free rebuild for those, and only those —
        // backend images read env at runtime and can keep the cache.
        environment.TryGetValue("framework", out var framework);
        if (CoolifyApiSupport.InlinesBuildTimeEnvironment(framework))
        {
            body["force"] = "true";
        }

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
        var seenEntries = 0;
        var idleRounds = 0;
        var failedWhileSilentRounds = 0;

        // 2s polls → 15 minutes of allowed silence. Docker builds with large layers go
        // quiet for far longer than the old 4-minute cap, which false-timed-out mid-build;
        // a deployment that truly hangs is ended by the terminal-status check, not by this.
        const int maxIdleRounds = 450;

        // How long a "failed" flag must hold with a silent log before it is believed.
        // Coolify flips the flag to failed while a compose deployment is still running
        // (the log kept growing and the same deployment later read "Finished"), so a
        // failed flag over a still-growing log means "still working", not "done".
        const int failedConfirmationRounds = 5;

        while (!cancellationToken.IsCancellationRequested && idleRounds < maxIdleRounds)
        {
            var deployment = await GetDeploymentAsync(session, deploymentId, cancellationToken);
            var entries = ParseLogEntries(deployment.Logs);
            var logGrew = entries.Count > seenEntries;

            if (logGrew)
            {
                // Counted by entry rather than by string length: the log is a JSON array, so it
                // grows in the middle as well as at the end and a length diff yields fragments
                // of JSON rather than whole lines.
                foreach (var entry in entries.Skip(seenEntries))
                {
                    yield return entry;
                }

                seenEntries = entries.Count;
                idleRounds = 0;
            }
            else
            {
                idleRounds++;
            }

            var mapped = MapStatus(deployment);
            if (mapped.Status is DeploymentStatusKind.Success)
            {
                yield break;
            }

            if (mapped.Status is DeploymentStatusKind.Failed)
            {
                failedWhileSilentRounds = logGrew ? 0 : failedWhileSilentRounds + 1;
                if (failedWhileSilentRounds >= failedConfirmationRounds)
                {
                    yield break;
                }
            }
            else
            {
                failedWhileSilentRounds = 0;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    /// <summary>
    /// Coolify returns a deployment's logs as a JSON array of entries, not as text. Treating it
    /// as text put the raw JSON in front of the user. Entries marked hidden are Coolify's own
    /// bookkeeping commands, which it does not show either.
    /// </summary>
    internal static IReadOnlyList<string> ParseLogEntries(string? logs)
    {
        if (string.IsNullOrWhiteSpace(logs))
        {
            return [];
        }

        var trimmed = logs.TrimStart();
        if (!trimmed.StartsWith('['))
        {
            // Older instances, or a plain-text error body: fall back to line splitting.
            return logs
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(logs);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return [];
            }

            var lines = new List<string>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("hidden", out var hidden) &&
                    hidden.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    continue;
                }

                if (!entry.TryGetProperty("output", out var output) ||
                    output.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    continue;
                }

                foreach (var line in (output.GetString() ?? string.Empty)
                             .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    lines.Add(line);
                }
            }

            return lines;
        }
        catch (System.Text.Json.JsonException)
        {
            // A partially-written log is normal mid-deploy; the next poll gets a whole one.
            return [];
        }
    }

    private static DeploymentStatus MapStatus(CoolifyDeployment deployment)
    {
        var status = deployment.Status?.Trim().ToLowerInvariant();
        // deployment_url is an instance-relative page path ("project/.../deployment/..."),
        // not the app's address — normalizing it produced garbage like "https://project/...".
        // The app URL comes from the application's fqdn, resolved by callers.
        var deployUrl = (string?)null;

        return status switch
        {
            "finished" => new DeploymentStatus(DeploymentStatusKind.Success, deployUrl, null),
            "failed" or "cancelled" or "cancelled-by-user" => MapUnsuccessfulStatus(deployment, deployUrl),
            "queued" or "in_progress" or "running" => new DeploymentStatus(
                DeploymentStatusKind.InProgress,
                deployUrl,
                null),
            _ => new DeploymentStatus(DeploymentStatusKind.InProgress, deployUrl, null)
        };
    }

    /// <summary>
    /// Coolify's status flag lies for compose apps: a deployment read as "failed" mid-poll
    /// showed "Finished" in the UI minutes later, with its log ending in a completed rollout
    /// (observed repeatedly against v4.1.2). The log is the reliable witness — when it says
    /// the rollout finished, trust it over the flag.
    /// </summary>
    private static DeploymentStatus MapUnsuccessfulStatus(CoolifyDeployment deployment, string? deployUrl)
    {
        var lines = ParseLogEntries(deployment.Logs);
        if (lines.Any(IsRolloutCompletedMarker))
        {
            return new DeploymentStatus(DeploymentStatusKind.Success, deployUrl, null);
        }

        return new DeploymentStatus(
            DeploymentStatusKind.Failed,
            deployUrl,
            // Same JSON-array shape: the raw last "line" was a fragment of JSON.
            lines.LastOrDefault() ?? "Publishing did not go through on Coolify.");
    }

    // Coolify's own end-of-rollout lines. "New container started." closes a compose/rolling
    // update; "Rolling update completed." closes a single-container one.
    internal static bool IsRolloutCompletedMarker(string line) =>
        line.Contains("New container started", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Rolling update completed", StringComparison.OrdinalIgnoreCase);

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

        [JsonPropertyName("status")]
        public string? Status { get; set; }
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
