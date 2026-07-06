using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

public interface IFrontendEnvironmentWiringService
{
    Task WireWebsiteTargetBeforeDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken);

    Task WireServerTargetAfterWebsiteDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> VerifyWiredEndpointsAsync(
        Guid deploymentId,
        CancellationToken cancellationToken);
}

public sealed class FrontendEnvironmentWiringService : IFrontendEnvironmentWiringService
{
    private readonly DeployAIDbContext _db;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IProviderServiceOperationsFactory _serviceOperationsFactory;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IHttpClientFactory _httpClientFactory;

    public FrontendEnvironmentWiringService(
        DeployAIDbContext db,
        IProviderManagementFactory managementFactory,
        IProviderServiceOperationsFactory serviceOperationsFactory,
        IProviderCredentialTokenService tokens,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _managementFactory = managementFactory;
        _serviceOperationsFactory = serviceOperationsFactory;
        _tokens = tokens;
        _httpClientFactory = httpClientFactory;
    }

    public async Task WireWebsiteTargetBeforeDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(websiteTarget.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var context = await LoadDualTargetContextAsync(deploymentId, websiteTarget, cancellationToken);
        if (context is null || string.IsNullOrWhiteSpace(context.ApiUrl))
        {
            return;
        }

        var websiteConfig = DeployTargetConfig.Parse(context.WebsiteDeployTarget.ConfigJson);
        var credentials = await GetCredentialsAsync(context.WebsiteDeployTarget, cancellationToken);
        var management = _managementFactory.GetManagement("vercel");
        var normalizedApiUrl = CrossProviderUrlWiring.NormalizeOrigin(context.ApiUrl);

        foreach (var key in CrossProviderUrlWiring.ResolveApiEnvKeys(websiteConfig.Framework))
        {
            await management.UpsertEnvVarAsync(
                credentials,
                context.WebsiteDeployTarget.ProviderProjectId,
                new UpsertProviderEnvVarRequest(
                    key,
                    normalizedApiUrl,
                    "encrypted",
                    ["production", "preview", "development"]),
                cancellationToken);
        }

        if (CrossProviderUrlWiring.UsesRelativeApiPaths(websiteConfig.Framework) &&
            management is IWebsiteApiProxySupport proxySupport)
        {
            await proxySupport.EnsureApiProxyRoutesAsync(
                credentials,
                context.WebsiteDeployTarget.ProviderProjectId,
                normalizedApiUrl,
                cancellationToken);
        }
    }

    public async Task WireServerTargetAfterWebsiteDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(websiteTarget.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase) ||
            websiteTarget.Status != DeploymentStatuses.Success ||
            string.IsNullOrWhiteSpace(websiteTarget.DeployUrl))
        {
            return;
        }

        var context = await LoadDualTargetContextAsync(deploymentId, websiteTarget, cancellationToken);
        if (context?.ServerDeployTarget is null || context.ServerDeployTarget.Credential is null)
        {
            return;
        }

        var vercelManagement = _managementFactory.GetManagement("vercel");
        var websiteCredentials = await GetCredentialsAsync(context.WebsiteDeployTarget, cancellationToken);
        var publicWebsiteUrl = vercelManagement is IWebsiteApiProxySupport proxySupport
            ? await proxySupport.ResolvePublicWebsiteUrlAsync(
                websiteCredentials,
                context.WebsiteDeployTarget.ProviderProjectId,
                websiteTarget.DeployUrl,
                cancellationToken)
            : CrossProviderUrlWiring.NormalizeOrigin(websiteTarget.DeployUrl);

        if (string.IsNullOrWhiteSpace(publicWebsiteUrl))
        {
            return;
        }

        var serverConfig = DeployTargetConfig.Parse(context.ServerDeployTarget.ConfigJson);
        var railwayManagement = _managementFactory.GetManagement("railway");
        var serverCredentials = await GetCredentialsAsync(context.ServerDeployTarget, cancellationToken);
        var corsKeys = CrossProviderUrlWiring.ResolveServerCorsEnvKeys(serverConfig.Framework);

        foreach (var key in corsKeys)
        {
            await railwayManagement.UpsertEnvVarAsync(
                serverCredentials,
                context.ServerDeployTarget.ProviderProjectId,
                new UpsertProviderEnvVarRequest(
                    key,
                    publicWebsiteUrl,
                    "plain",
                    []),
                cancellationToken);
        }

        var serviceOperations = _serviceOperationsFactory.GetServiceOperations("railway");
        if (serviceOperations is not null)
        {
            await serviceOperations.RedeployServiceAsync(
                serverCredentials,
                context.ServerDeployTarget.ProviderProjectId,
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> VerifyWiredEndpointsAsync(
        Guid deploymentId,
        CancellationToken cancellationToken)
    {
        var deployment = await _db.Deployments
            .Include(d => d.Targets)
            .Include(d => d.Project)
            .ThenInclude(p => p.DeployTargets)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, cancellationToken);

        if (deployment is null)
        {
            return [];
        }

        var websiteTarget = deployment.Targets.FirstOrDefault(t =>
            string.Equals(t.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase) &&
            t.Status == DeploymentStatuses.Success);
        var serverTarget = deployment.Targets.FirstOrDefault(t =>
            string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
            t.Status == DeploymentStatuses.Success);

        if (websiteTarget is null || serverTarget is null ||
            string.IsNullOrWhiteSpace(websiteTarget.DeployUrl) ||
            string.IsNullOrWhiteSpace(serverTarget.DeployUrl))
        {
            return [];
        }

        var websiteDeployTarget = deployment.Project.DeployTargets
            .FirstOrDefault(t => t.Id == websiteTarget.DeployTargetId);
        var websiteConfig = DeployTargetConfig.Parse(websiteDeployTarget?.ConfigJson);
        var messages = new List<string>();
        var client = _httpClientFactory.CreateClient(nameof(FrontendEnvironmentWiringService));
        client.Timeout = TimeSpan.FromSeconds(20);

        var apiUrl = CrossProviderUrlWiring.NormalizeOrigin(serverTarget.DeployUrl);
        var websiteUrl = CrossProviderUrlWiring.NormalizeOrigin(websiteTarget.DeployUrl);

        if (websiteDeployTarget?.Credential is not null)
        {
            var vercelManagement = _managementFactory.GetManagement("vercel");
            if (vercelManagement is IWebsiteApiProxySupport proxySupport)
            {
                var credentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
                var resolved = await proxySupport.ResolvePublicWebsiteUrlAsync(
                    credentials,
                    websiteDeployTarget.ProviderProjectId,
                    websiteTarget.DeployUrl,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    websiteUrl = CrossProviderUrlWiring.NormalizeOrigin(resolved);
                }
            }
        }

        await VerifyReachableAsync(
            client,
            $"{apiUrl}/",
            label: "Railway API",
            messages,
            cancellationToken);

        if (CrossProviderUrlWiring.UsesRelativeApiPaths(websiteConfig?.Framework))
        {
            await VerifyProxiedApiHealthAsync(
                client,
                $"{websiteUrl}/api/health",
                label: "Vercel website proxy",
                messages,
                cancellationToken);
        }

        await VerifyCorsHeaderAsync(
            client,
            $"{apiUrl}/",
            websiteUrl,
            label: "Railway CORS",
            messages,
            cancellationToken);

        return messages;
    }

    internal static IReadOnlyList<string> ResolveApiEnvKeys(string? framework) =>
        CrossProviderUrlWiring.ResolveApiEnvKeys(framework);

    private async Task<ProviderCredentials> GetCredentialsAsync(
        DeployTarget deployTarget,
        CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync(deployTarget.Credential!, cancellationToken);
        return new ProviderCredentials(token);
    }

    private async Task<DualTargetContext?> LoadDualTargetContextAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken)
    {
        var deployment = await _db.Deployments
            .Include(d => d.Targets)
            .Include(d => d.Project)
            .ThenInclude(p => p.DeployTargets)
            .ThenInclude(t => t.Credential)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, cancellationToken);

        if (deployment is null)
        {
            return null;
        }

        var websiteDeployTarget = deployment.Project.DeployTargets
            .FirstOrDefault(t => t.Id == websiteTarget.DeployTargetId);
        if (websiteDeployTarget?.Credential is null)
        {
            return null;
        }

        var serverDeploymentTarget = deployment.Targets
            .FirstOrDefault(t =>
                string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
                t.Status == DeploymentStatuses.Success &&
                !string.IsNullOrWhiteSpace(t.DeployUrl));

        var serverDeployTarget = serverDeploymentTarget is null
            ? null
            : deployment.Project.DeployTargets.FirstOrDefault(t => t.Id == serverDeploymentTarget.DeployTargetId);

        return new DualTargetContext(
            websiteDeployTarget,
            serverDeployTarget,
            serverDeploymentTarget?.DeployUrl);
    }

    private static async Task VerifyReachableAsync(
        HttpClient client,
        string url,
        string label,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                messages.Add($"{label} check failed ({(int)response.StatusCode}): {url}");
                return;
            }

            messages.Add($"{label} check passed: {url}");
        }
        catch (Exception ex)
        {
            messages.Add($"{label} check error: {ex.Message}");
        }
    }

    private static async Task VerifyProxiedApiHealthAsync(
        HttpClient client,
        string url,
        string label,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";

            if (!response.IsSuccessStatusCode)
            {
                messages.Add($"{label} check failed ({(int)response.StatusCode}): {url}");
                return;
            }

            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add($"{label} check returned HTML instead of API JSON. Verify Vercel rewrites to the Railway API.");
                return;
            }

            if (!body.Contains("\"status\"", StringComparison.OrdinalIgnoreCase) &&
                !body.Contains("ok", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add($"{label} check returned an unexpected body: {url}");
                return;
            }

            messages.Add($"{label} check passed: {url}");
        }
        catch (Exception ex)
        {
            messages.Add($"{label} check error: {ex.Message}");
        }
    }

    private static async Task VerifyCorsHeaderAsync(
        HttpClient client,
        string url,
        string origin,
        string label,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Origin", origin);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                messages.Add($"{label} check failed ({(int)response.StatusCode}): {url}");
                return;
            }

            if (!response.Headers.Contains("Access-Control-Allow-Origin"))
            {
                messages.Add($"{label} check: server reachable but Access-Control-Allow-Origin is missing for {origin}. A server redeploy may still be in progress.");
                return;
            }

            messages.Add($"{label} check passed for origin {origin}.");
        }
        catch (Exception ex)
        {
            messages.Add($"{label} check error: {ex.Message}");
        }
    }

    private sealed record DualTargetContext(
        DeployTarget WebsiteDeployTarget,
        DeployTarget? ServerDeployTarget,
        string? ApiUrl);
}
