using System.Net.Http.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Coolify;

public sealed partial class CoolifyProvider : IProviderServiceOperations, IProviderLifecycleOperations
{
    public Task StartApplicationAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken) =>
        SendLifecycleActionAsync(credentials, providerProjectId, "start", cancellationToken);

    public Task StopApplicationAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken) =>
        SendLifecycleActionAsync(credentials, providerProjectId, "stop", cancellationToken);

    public Task RestartApplicationAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken) =>
        SendLifecycleActionAsync(credentials, providerProjectId, "restart", cancellationToken);

    private async Task SendLifecycleActionAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string action,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        using var request = CreateRequest(
            HttpMethod.Get,
            session,
            $"applications/{providerProjectId}/{action}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not {action} the Coolify application ({(int)response.StatusCode}).");
        }
    }

    public async Task<ProviderServiceStatus> GetServiceStatusAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var application = await TryGetApplicationAsync(session, providerProjectId, cancellationToken);
        if (application is null)
        {
            return new ProviderServiceStatus("unknown", null, null);
        }

        var deployUrl = NormalizeUrl(application.Fqdn);
        var status = MapApplicationStatus(application.Status, deployUrl);
        return new ProviderServiceStatus(status, deployUrl, null);
    }

    public async Task RedeployServiceAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        using var request = CreateRequest(HttpMethod.Post, session, "deploy");
        request.Content = JsonContent.Create(new Dictionary<string, string> { ["uuid"] = providerProjectId });
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Coolify redeploy failed ({(int)response.StatusCode}).");
        }
    }

    public Task DeleteServiceAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken) =>
        DeleteProjectAsync(credentials, providerProjectId, cancellationToken);

    // Coolify has no "promote this old deployment" API — a rollback means redeploying an
    // earlier commit. Point at what does work rather than leaving a bare "unsupported".
    public Task RollbackDeploymentAsync(
        ProviderCredentials credentials,
        string providerDeploymentId,
        CancellationToken cancellationToken) =>
        throw new DeployAIException(
            "unsupported_provider",
            "Coolify can't promote a previous deployment. Redeploy the commit you want, or stop " +
            "the app while you sort the problem out.");

    private static string MapApplicationStatus(string? status, string? deployUrl)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return string.IsNullOrWhiteSpace(deployUrl) ? "not_deployed" : "running";
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "running" or "started" or "active" or "finished" => "running",
            "exited" or "stopped" or "failed" or "error" => "failed",
            "building" or "deploying" or "in_progress" or "queued" => "deploying",
            _ => string.IsNullOrWhiteSpace(deployUrl) ? "unknown" : "running"
        };
    }
}
