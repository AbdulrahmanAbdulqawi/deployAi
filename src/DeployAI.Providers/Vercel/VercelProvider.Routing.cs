using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Vercel;

public sealed partial class VercelProvider : IWebsiteApiProxySupport
{
    public async Task EnsureApiProxyRoutesAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        var projectId = await ResolveProjectIdAsync(credentials, providerProjectId, cancellationToken);
        var normalizedApi = VercelApiSupport.NormalizeExternalOrigin(apiBaseUrl);

        await UpsertProxyRouteAsync(
            credentials,
            projectId,
            CrossProviderRouteNames.ApiProxy,
            "/api/:path*",
            $"{normalizedApi}/api/:path*",
            cancellationToken);

        await UpsertProxyRouteAsync(
            credentials,
            projectId,
            CrossProviderRouteNames.HubsProxy,
            "/hubs/:path*",
            $"{normalizedApi}/hubs/:path*",
            cancellationToken);

        await PromoteStagedRoutesAsync(credentials, projectId, cancellationToken);
    }

    public async Task<string?> ResolvePublicWebsiteUrlAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string? deploymentUrl,
        CancellationToken cancellationToken)
    {
        var projectId = await ResolveProjectIdAsync(credentials, providerProjectId, cancellationToken);
        var project = await GetProjectAsync(credentials, projectId, cancellationToken);
        var alias = VercelApiSupport.ExtractPrimaryProductionAlias(project.Aliases, project.Name);
        if (!string.IsNullOrWhiteSpace(alias))
        {
            return VercelApiSupport.NormalizeExternalOrigin(alias);
        }

        if (!string.IsNullOrWhiteSpace(deploymentUrl))
        {
            return VercelApiSupport.NormalizeExternalOrigin(deploymentUrl);
        }

        return null;
    }

    private async Task<string> ResolveProjectIdAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        if (providerProjectId.StartsWith("prj_", StringComparison.Ordinal))
        {
            return providerProjectId;
        }

        var project = await GetProjectAsync(credentials, providerProjectId, cancellationToken);
        return project.Id;
    }

    private async Task UpsertProxyRouteAsync(
        ProviderCredentials credentials,
        string projectId,
        string routeName,
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var existingRouteId = await FindRouteIdByNameAsync(credentials, projectId, routeName, cancellationToken);
        if (existingRouteId is null)
        {
            await CreateProxyRouteAsync(credentials, projectId, routeName, source, destination, cancellationToken);
            return;
        }

        await UpdateProxyRouteAsync(
            credentials,
            projectId,
            existingRouteId,
            routeName,
            source,
            destination,
            cancellationToken);
    }

    private async Task<string?> FindRouteIdByNameAsync(
        ProviderCredentials credentials,
        string projectId,
        string routeName,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"v1/projects/{Uri.EscapeDataString(projectId)}/routes", credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<VercelRoutesListResponse>(cancellationToken);
        if (payload?.Routes is null)
        {
            return null;
        }

        foreach (var route in payload.Routes)
        {
            if (string.Equals(route.Name, routeName, StringComparison.Ordinal))
            {
                return route.Id;
            }
        }

        return null;
    }

    private async Task CreateProxyRouteAsync(
        ProviderCredentials credentials,
        string projectId,
        string routeName,
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"v1/projects/{Uri.EscapeDataString(projectId)}/routes",
            credentials.Token);
        request.Content = JsonContent.Create(BuildRouteBody(routeName, source, destination));
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task UpdateProxyRouteAsync(
        ProviderCredentials credentials,
        string projectId,
        string routeId,
        string routeName,
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            $"v1/projects/{Uri.EscapeDataString(projectId)}/routes/{Uri.EscapeDataString(routeId)}",
            credentials.Token);
        request.Content = JsonContent.Create(BuildRouteBody(routeName, source, destination));
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task PromoteStagedRoutesAsync(
        ProviderCredentials credentials,
        string projectId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"v1/projects/{Uri.EscapeDataString(projectId)}/routes/versions",
            credentials.Token);
        request.Content = JsonContent.Create(new { action = "promote" });
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
    }

    private static object BuildRouteBody(string routeName, string source, string destination) =>
        new
        {
            route = new
            {
                name = routeName,
                enabled = true,
                route = new
                {
                    src = source,
                    dest = destination
                }
            }
        };

    private static class CrossProviderRouteNames
    {
        internal const string ApiProxy = "DeployAI API Proxy";
        internal const string HubsProxy = "DeployAI Hubs Proxy";
    }

    private sealed class VercelRoutesListResponse
    {
        [JsonPropertyName("routes")]
        public List<VercelRouteSummary>? Routes { get; set; }
    }

    private sealed class VercelRouteSummary
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
