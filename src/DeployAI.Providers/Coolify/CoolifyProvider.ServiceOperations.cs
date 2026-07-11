using System.Net.Http.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Coolify;

public sealed partial class CoolifyProvider : IProviderServiceOperations
{
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

    public Task RollbackDeploymentAsync(
        ProviderCredentials credentials,
        string providerDeploymentId,
        CancellationToken cancellationToken) =>
        throw new DeployAIException(
            "unsupported_provider",
            "Coolify does not support deployment rollback through DeployAI yet.");

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
