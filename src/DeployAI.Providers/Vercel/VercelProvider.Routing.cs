using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Vercel;

/// <summary>Manages Vercel's project-level routing rules (for same-origin API proxying) and resolves a project's live production domains.</summary>
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
        var aliases = await CollectProductionWebsiteAliasesAsync(
            credentials,
            projectId,
            project,
            cancellationToken);
        var alias = VercelApiSupport.ExtractPrimaryProductionAlias(aliases, project.Name);
        if (!string.IsNullOrWhiteSpace(alias))
        {
            return VercelApiSupport.NormalizeExternalOrigin(alias);
        }

        if (!string.IsNullOrWhiteSpace(deploymentUrl) &&
            !VercelApiSupport.IsPreviewAlias(deploymentUrl))
        {
            return VercelApiSupport.NormalizeExternalOrigin(deploymentUrl);
        }

        return null;
    }

    public async Task<IReadOnlyList<string>> ListProductionWebsiteOriginsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var projectId = await ResolveProjectIdAsync(credentials, providerProjectId, cancellationToken);
        var project = await GetProjectAsync(credentials, projectId, cancellationToken);
        var aliases = await CollectProductionWebsiteAliasesAsync(
            credentials,
            projectId,
            project,
            cancellationToken);

        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias) && !VercelApiSupport.IsPreviewAlias(alias))
            .Select(alias => VercelApiSupport.NormalizeExternalOrigin(alias!))
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(origin => origin, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> CollectProductionWebsiteAliasesAsync(
        ProviderCredentials credentials,
        string projectId,
        VercelProjectWithLink project,
        CancellationToken cancellationToken)
    {
        var aliases = new List<string>();
        if (project.Aliases is not null)
        {
            aliases.AddRange(project.Aliases);
        }

        aliases.AddRange(await ListProductionDomainNamesAsync(credentials, projectId, project.AccountId, cancellationToken));
        aliases.AddRange(await GetLatestProductionDeploymentAliasesAsync(credentials, projectId, project.AccountId, cancellationToken));
        return aliases;
    }

    private async Task<IReadOnlyList<string>> ListProductionDomainNamesAsync(
        ProviderCredentials credentials,
        string projectId,
        string? accountId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            AppendTeamQuery(
                $"v9/projects/{Uri.EscapeDataString(projectId)}/domains?production=true",
                accountId),
            credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var payload = await response.Content.ReadFromJsonAsync<VercelProjectDomainsResponse>(cancellationToken);
        if (payload?.Domains is null)
        {
            return [];
        }

        return payload.Domains
            .Select(domain => domain.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> GetLatestProductionDeploymentAliasesAsync(
        ProviderCredentials credentials,
        string projectId,
        string? accountId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            AppendTeamQuery(
                $"v6/deployments?projectId={Uri.EscapeDataString(projectId)}&target=production&limit=1",
                accountId),
            credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var payload = await response.Content.ReadFromJsonAsync<VercelDeploymentsListResponse>(cancellationToken);
        var deployment = payload?.Deployments?.FirstOrDefault();
        if (deployment is null)
        {
            return [];
        }

        var aliases = new List<string>();
        if (deployment.Aliases is not null)
        {
            aliases.AddRange(deployment.Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias))!);
        }

        if (!string.IsNullOrWhiteSpace(deployment.Url))
        {
            aliases.Add(deployment.Url);
        }

        return aliases;
    }

    public async Task<string?> GetLatestProductionDeploymentIdAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var projectId = await ResolveProjectIdAsync(credentials, providerProjectId, cancellationToken);
        var project = await GetProjectAsync(credentials, projectId, cancellationToken);
        using var request = CreateRequest(
            HttpMethod.Get,
            AppendTeamQuery(
                $"v6/deployments?projectId={Uri.EscapeDataString(projectId)}&target=production&limit=20",
                project.AccountId),
            credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<VercelDeploymentsListResponse>(cancellationToken);
        return payload?.Deployments?
            .FirstOrDefault(deployment =>
                string.Equals(deployment.ReadyState, "READY", StringComparison.OrdinalIgnoreCase))
            ?.Uid;
    }

    public async Task AssignProductionDomainsToDeploymentAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        var projectId = await ResolveProjectIdAsync(credentials, providerProjectId, cancellationToken);
        var project = await GetProjectAsync(credentials, projectId, cancellationToken);
        var domains = await ListProductionDomainNamesAsync(credentials, projectId, project.AccountId, cancellationToken);
        if (domains.Count == 0)
        {
            return;
        }

        foreach (var domain in domains.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var request = CreateRequest(
                HttpMethod.Post,
                AppendTeamQuery(
                    $"v2/deployments/{Uri.EscapeDataString(deploymentId)}/aliases",
                    project.AccountId),
                credentials.Token);
            request.Content = JsonContent.Create(new { alias = domain });
            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode &&
                response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = VercelApiSupport.ParseErrorMessage(body) ?? string.Empty;
                if (message.Contains("already associated", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!response.IsSuccessStatusCode &&
                response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = VercelApiSupport.ParseErrorMessage(body) ?? string.Empty;
                if (message.Contains("readyState", StringComparison.OrdinalIgnoreCase) &&
                    message.Contains("READY", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
        }
    }

    private static string AppendTeamQuery(string path, string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || !accountId.StartsWith("team_", StringComparison.Ordinal))
        {
            return path;
        }

        var separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{path}{separator}teamId={Uri.EscapeDataString(accountId)}";
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

    /// <summary>Vercel routing changes are staged and only take effect once promoted - finds the non-live version to promote and activates it.</summary>
    private async Task PromoteStagedRoutesAsync(
        ProviderCredentials credentials,
        string projectId,
        CancellationToken cancellationToken)
    {
        var versionId = await GetRouteVersionIdToPromoteAsync(credentials, projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new DeployAI.Core.Exceptions.DeployAIException(
                "vercel_route_promote_failed",
                "Vercel API proxy routes were updated but no staged routing version was available to promote.");
        }

        using var request = CreateRequest(
            HttpMethod.Post,
            $"v1/projects/{Uri.EscapeDataString(projectId)}/routes/versions",
            credentials.Token);
        request.Content = JsonContent.Create(new { id = versionId, action = "promote" });
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<string?> GetRouteVersionIdToPromoteAsync(
        ProviderCredentials credentials,
        string projectId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"v1/projects/{Uri.EscapeDataString(projectId)}/routes/versions",
            credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<VercelRouteVersionsResponse>(cancellationToken);
        if (payload?.Versions is null || payload.Versions.Count == 0)
        {
            return null;
        }

        var staging = payload.Versions.FirstOrDefault(version => version.IsStaging == true);
        if (!string.IsNullOrWhiteSpace(staging?.Id))
        {
            return staging.Id;
        }

        return payload.Versions
            .Where(version => version.IsLive != true && !string.IsNullOrWhiteSpace(version.Id))
            .OrderByDescending(version => version.LastModified ?? 0)
            .Select(version => version.Id)
            .FirstOrDefault();
    }

    private static object BuildRouteBody(string routeName, string source, string destination) =>
        new
        {
            route = new
            {
                name = routeName,
                enabled = true,
                srcSyntax = "path-to-regexp",
                route = new
                {
                    src = source,
                    dest = destination,
                    @override = true,
                    important = true
                }
            },
            position = new
            {
                placement = "start"
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

    private sealed class VercelRouteVersionsResponse
    {
        [JsonPropertyName("versions")]
        public List<VercelRouteVersion>? Versions { get; set; }
    }

    private sealed class VercelRouteVersion
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("isStaging")]
        public bool? IsStaging { get; set; }

        [JsonPropertyName("isLive")]
        public bool? IsLive { get; set; }

        [JsonPropertyName("lastModified")]
        public long? LastModified { get; set; }
    }

    private sealed class VercelRouteSummary
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class VercelProjectDomainsResponse
    {
        [JsonPropertyName("domains")]
        public List<VercelProjectDomain>? Domains { get; set; }
    }

    private sealed class VercelProjectDomain
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class VercelDeploymentsListResponse
    {
        [JsonPropertyName("deployments")]
        public List<VercelDeploymentSummary>? Deployments { get; set; }
    }

    private sealed class VercelDeploymentSummary
    {
        [JsonPropertyName("uid")]
        public string? Uid { get; set; }

        [JsonPropertyName("readyState")]
        public string? ReadyState { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("alias")]
        public List<string>? Aliases { get; set; }
    }
}
