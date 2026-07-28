using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>Which side of a deployment to run live verification checks against.</summary>
public enum DeploymentVerificationScope
{
    Website,
    Server,
    Both
}

/// <summary>One live post-deploy check's result (reachability, CORS, split-origin wiring, health, etc.).</summary>
public sealed record DeploymentVerificationCheck(
    string Id,
    string Target,
    string Label,
    string Status,
    string Message,
    string? Url,
    string? SuggestedAction,
    bool CanRequestClaudeFix,
    IReadOnlyList<string> ReferencedFiles);

/// <summary>The full result of verifying a deployment - overall success and every individual check.</summary>
public sealed record DeploymentVerificationResult(
    bool Success,
    string Scope,
    IReadOnlyList<DeploymentVerificationCheck> Checks,
    DateTimeOffset CompletedAt);

/// <summary>Runs live post-deploy verification checks against a deployment's actual website/server URLs.</summary>
public interface IDeploymentVerificationService
{
    /// <summary>Runs verification checks for one or both sides of a deployment. If more than one complete provider pair exists, verifies every pair (see <see cref="DeploymentTargetResolution.ResolveProviderPairs(System.Collections.Generic.IEnumerable{DeployAI.Data.Entities.DeploymentTarget})"/>) and merges the results.</summary>
    Task<DeploymentVerificationResult> VerifyAsync(
        Guid deploymentId,
        DeploymentVerificationScope scope,
        CancellationToken cancellationToken);
}

/// <summary>
/// Probes a deployment's actual live URLs to verify it's really working: homepage reachability,
/// SPA shell/split-origin bundle wiring, CORS, and API health - as opposed to just trusting the
/// provider's own "deployed successfully" status.
/// </summary>
public sealed class DeploymentVerificationService : IDeploymentVerificationService
{
    private readonly DeployAIDbContext _db;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IProviderServiceOperationsFactory _serviceOperationsFactory;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IHttpClientFactory _httpClientFactory;

    public DeploymentVerificationService(
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

    public async Task<DeploymentVerificationResult> VerifyAsync(
        Guid deploymentId,
        DeploymentVerificationScope scope,
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
            return EmptyResult(scope, "Deployment not found.");
        }

        // A deployment normally has at most one complete (website, server) pair, in which case
        // this falls back to the original single-pass resolution below (byte-identical behavior,
        // including partial verification when only one side is deployed). But a project can have
        // *two* complete pairs at once - e.g. Vercel+Railway alongside a Coolify+Coolify full
        // stack - and FindWebsiteTarget/FindServerTarget would silently verify only the first pair
        // found, leaving the other pair permanently unverified. When more than one complete pair
        // exists, verify every pair and merge the results instead.
        var pairs = DeploymentTargetResolution.ResolveProviderPairs(deployment.Targets);
        if (pairs.Count > 1)
        {
            var pairResults = new List<DeploymentVerificationResult>();
            foreach (var pair in pairs)
            {
                pairResults.Add(await VerifyPairAsync(deployment, pair.Website, pair.Server, scope, cancellationToken));
            }

            var allChecks = new List<DeploymentVerificationCheck>();
            for (var i = 0; i < pairResults.Count; i++)
            {
                var pairLabel = $"{pairs[i].Website.ProviderName}+{pairs[i].Server.ProviderName}";
                allChecks.AddRange(pairResults[i].Checks.Select(check => check with { Id = $"{pairLabel}:{check.Id}" }));
            }

            return new DeploymentVerificationResult(
                pairResults.All(result => result.Success),
                scope.ToString().ToLowerInvariant(),
                allChecks,
                DateTimeOffset.UtcNow);
        }

        var websiteTarget = DeploymentTargetResolution.FindWebsiteTarget(deployment.Targets);
        var serverTarget = DeploymentTargetResolution.FindServerTarget(deployment.Targets);
        return await VerifyPairAsync(deployment, websiteTarget, serverTarget, scope, cancellationToken);
    }

    /// <summary>Runs all checks for one website/server pair (either side may be null - checks for a missing side are skipped, not failed).</summary>
    private async Task<DeploymentVerificationResult> VerifyPairAsync(
        Deployment deployment,
        DeploymentTarget? websiteTarget,
        DeploymentTarget? serverTarget,
        DeploymentVerificationScope scope,
        CancellationToken cancellationToken)
    {
        var coolifyFullStack =
            websiteTarget is not null &&
            serverTarget is not null &&
            string.Equals(websiteTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(serverTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);

        var websiteDeployTarget = ResolveWebsiteDeployTarget(deployment, websiteTarget);
        var serverDeployTarget = ResolveServerDeployTarget(deployment, serverTarget);

        var websiteConfig = DeployTargetConfig.Parse(websiteDeployTarget?.ConfigJson);
        var serverConfig = DeployTargetConfig.Parse(serverDeployTarget?.ConfigJson);

        var checks = new List<DeploymentVerificationCheck>();
        var includeWebsite = scope is DeploymentVerificationScope.Website or DeploymentVerificationScope.Both;
        var includeServer = scope is DeploymentVerificationScope.Server or DeploymentVerificationScope.Both;
        var includeConnection = websiteTarget is not null && serverTarget is not null;

        string? websiteUrl = null;
        string? apiUrl = null;

        if (includeWebsite)
        {
            if (websiteTarget is null)
            {
                checks.Add(Skipped("website.missing", "website", "Website target", "No website target on this deployment."));
            }
            else if (websiteTarget.Status != DeploymentStatuses.Success)
            {
                checks.Add(Skipped(
                    "website.not_deployed",
                    "website",
                    "Website target",
                    $"Website deployment status is {websiteTarget.Status}."));
            }
            else if (string.IsNullOrWhiteSpace(websiteTarget.DeployUrl))
            {
                checks.Add(Skipped("website.no_url", "website", "Website URL", "Website deployed without a public URL."));
            }
            else
            {
                websiteUrl = CrossProviderUrlWiring.NormalizeOrigin(websiteTarget.DeployUrl);
                if (websiteDeployTarget?.Credential is not null)
                {
                    websiteUrl = await ResolveWebsiteUrlAsync(
                        websiteDeployTarget,
                        websiteTarget.DeployUrl,
                        websiteUrl,
                        cancellationToken) ?? websiteUrl;
                }

                await RunWebsiteChecksAsync(
                    checks,
                    websiteUrl,
                    websiteConfig?.Framework,
                    serverConfig?.Framework,
                    coolifyFullStack,
                    cancellationToken);
            }
        }

        if (includeServer)
        {
            if (serverTarget is null)
            {
                checks.Add(Skipped("server.missing", "server", "Server target", "No server target on this deployment."));
            }
            else if (serverTarget.Status != DeploymentStatuses.Success)
            {
                checks.Add(Skipped(
                    "server.not_deployed",
                    "server",
                    "Server target",
                    $"Server deployment status is {serverTarget.Status}."));
            }
            else if (string.IsNullOrWhiteSpace(serverTarget.DeployUrl))
            {
                checks.Add(Skipped("server.no_url", "server", "Server URL", "Server deployed without a public URL."));
            }
            else
            {
                apiUrl = CrossProviderUrlWiring.NormalizeOrigin(serverTarget.DeployUrl);
                await RunServerChecksAsync(
                    checks,
                    apiUrl,
                    websiteConfig?.Framework,
                    serverConfig?.Framework,
                    websiteUrl,
                    coolifyFullStack,
                    cancellationToken);
            }
        }

        if (includeConnection)
        {
            websiteUrl ??= await TryResolveWebsiteUrlAsync(
                websiteTarget,
                websiteDeployTarget,
                cancellationToken);
            apiUrl ??= await TryResolveApiUrlAsync(
                serverTarget,
                serverDeployTarget,
                cancellationToken);

            if (websiteTarget is null || serverTarget is null)
            {
                checks.Add(Skipped(
                    "connection.missing_targets",
                    "connection",
                    "Cross-origin wiring",
                    "Connection checks require both website and server targets."));
            }
            else if (websiteTarget.Status != DeploymentStatuses.Success ||
                     serverTarget.Status != DeploymentStatuses.Success)
            {
                checks.Add(Skipped(
                    "connection.not_ready",
                    "connection",
                    "Cross-origin wiring",
                    "Connection checks require both targets to deploy successfully."));
            }
            else if (string.IsNullOrWhiteSpace(websiteUrl) || string.IsNullOrWhiteSpace(apiUrl))
            {
                checks.Add(Skipped(
                    "connection.no_urls",
                    "connection",
                    "Cross-origin wiring",
                    BuildMissingConnectionUrlMessage(websiteUrl, apiUrl)));
            }
            else
            {
                await RunConnectionChecksAsync(
                    checks,
                    websiteUrl,
                    apiUrl,
                    websiteConfig?.Framework,
                    serverConfig?.Framework,
                    coolifyFullStack,
                    cancellationToken);
            }
        }

        var success = checks.All(c => c.Status is "passed" or "skipped");

        return new DeploymentVerificationResult(
            success,
            scope.ToString().ToLowerInvariant(),
            checks,
            DateTimeOffset.UtcNow);
    }

    /// <summary>Checks the website's homepage reachability, SPA shell markers, and (for split-origin stacks) that the deployed bundle actually calls the API directly.</summary>
    private async Task RunWebsiteChecksAsync(
        List<DeploymentVerificationCheck> checks,
        string websiteUrl,
        string? websiteFramework,
        string? serverFramework,
        bool coolifyFullStack,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var homepageUrl = $"{websiteUrl.TrimEnd('/')}/";

        var homepage = await DeploymentEndpointProbes.CheckWebsiteHomepageAsync(client, websiteUrl, cancellationToken);
        checks.Add(ToCheck("website.reachable", "website", "Website homepage", homepageUrl, homepage, coolifyFullStack));

        if (homepage.Status == ProbeCheckStatus.Passed)
        {
            try
            {
                var html = await client.GetStringAsync(homepageUrl, cancellationToken);
                var shell = DeploymentEndpointProbes.CheckSpaShell(html);
                checks.Add(ToCheck("website.spa_shell", "website", "SPA shell", homepageUrl, shell, coolifyFullStack));
            }
            catch (Exception ex)
            {
                checks.Add(Failed(
                    "website.spa_shell",
                    "website",
                    "SPA shell",
                    homepageUrl,
                    $"Could not read homepage HTML: {ex.Message}",
                    "redeploy_website",
                    coolifyFullStack));
            }
        }

        if (homepage.Status == ProbeCheckStatus.Passed &&
            CrossProviderUrlWiring.ShouldUseSplitOrigin(websiteFramework, serverFramework))
        {
            var wiring = await DeploymentEndpointProbes.CheckSplitOriginSpaWiringAsync(client, websiteUrl, cancellationToken);
            if (wiring is not null)
            {
                checks.Add(ToCheck(
                    "website.split_origin_wiring",
                    "website",
                    "Split-origin bundle wiring",
                    homepageUrl,
                    wiring,
                    coolifyFullStack));
            }
        }
    }

    /// <summary>Checks the server's reachability and health endpoint.</summary>
    private async Task RunServerChecksAsync(
        List<DeploymentVerificationCheck> checks,
        string apiUrl,
        string? websiteFramework,
        string? serverFramework,
        string? websiteUrl,
        bool coolifyFullStack,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var rootUrl = $"{apiUrl.TrimEnd('/')}/";

        var reachable = await DeploymentEndpointProbes.CheckReachableAsync(client, rootUrl, cancellationToken);
        checks.Add(ToCheck("server.reachable", "server", "API reachable", rootUrl, reachable, coolifyFullStack));

        if (CrossProviderUrlWiring.ShouldUseSplitOrigin(websiteFramework, serverFramework))
        {
            var healthUrl = $"{apiUrl.TrimEnd('/')}/api/v1/health";
            var health = await DeploymentEndpointProbes.CheckSplitOriginApiHealthAsync(client, healthUrl, cancellationToken);
            checks.Add(ToCheck("server.health", "server", "API health", healthUrl, health, coolifyFullStack));
        }
        else if (CrossProviderUrlWiring.UsesRelativeApiPaths(websiteFramework) && !string.IsNullOrWhiteSpace(websiteUrl))
        {
            var healthUrl = $"{websiteUrl.TrimEnd('/')}/api/health";
            var health = await DeploymentEndpointProbes.CheckProxiedApiHealthAsync(client, healthUrl, cancellationToken);
            checks.Add(ToCheck("server.health", "server", "API health (via website proxy)", healthUrl, health, coolifyFullStack));
        }
        else
        {
            var healthUrl = $"{apiUrl.TrimEnd('/')}/api/health";
            var health = await DeploymentEndpointProbes.CheckProxiedApiHealthAsync(client, healthUrl, cancellationToken);
            checks.Add(ToCheck("server.health", "server", "API health", healthUrl, health, coolifyFullStack));
        }
    }

    /// <summary>Checks CORS (the server accepts requests from the website's origin) and that the API is reachable from the website's declared API URL.</summary>
    private async Task RunConnectionChecksAsync(
        List<DeploymentVerificationCheck> checks,
        string websiteUrl,
        string apiUrl,
        string? websiteFramework,
        string? serverFramework,
        bool coolifyFullStack,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();

        if (CrossProviderUrlWiring.ShouldUseSplitOrigin(websiteFramework, serverFramework))
        {
            var healthUrl = $"{apiUrl.TrimEnd('/')}/api/v1/health";
            var health = await DeploymentEndpointProbes.CheckSplitOriginApiHealthAsync(client, healthUrl, cancellationToken);
            checks.Add(ToCheck("connection.api_health", "connection", "API health (split-origin)", healthUrl, health, coolifyFullStack));
        }
        else if (CrossProviderUrlWiring.UsesRelativeApiPaths(websiteFramework))
        {
            var proxy = await DeploymentEndpointProbes.CheckProxiedApiLoginAsync(client, websiteUrl, cancellationToken);
            checks.Add(ToCheck(
                "connection.website_proxy",
                "connection",
                "Website API proxy",
                $"{websiteUrl.TrimEnd('/')}/api/v1/auth/login",
                proxy,
                coolifyFullStack));
        }

        var cors = await DeploymentEndpointProbes.CheckCorsHeaderAsync(client, apiUrl, websiteUrl, cancellationToken);
        checks.Add(ToCheck("connection.cors", "connection", "CORS", apiUrl, cors, coolifyFullStack));
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(nameof(DeploymentVerificationService));
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private async Task<string?> ResolveWebsiteUrlAsync(
        DeployTarget websiteDeployTarget,
        string deploymentUrl,
        string fallbackUrl,
        CancellationToken cancellationToken)
    {
        var vercelManagement = _managementFactory.GetManagement("vercel");
        if (vercelManagement is not IWebsiteApiProxySupport proxySupport)
        {
            return fallbackUrl;
        }

        var credentials = new ProviderCredentials(
            await _tokens.GetTokenAsync(websiteDeployTarget.Credential!, cancellationToken));
        var resolved = await proxySupport.ResolvePublicWebsiteUrlAsync(
            credentials,
            websiteDeployTarget.ProviderProjectId,
            deploymentUrl,
            cancellationToken);

        return string.IsNullOrWhiteSpace(resolved)
            ? fallbackUrl
            : CrossProviderUrlWiring.NormalizeOrigin(resolved);
    }

    private async Task<string?> TryResolveWebsiteUrlAsync(
        DeploymentTarget? websiteTarget,
        DeployTarget? websiteDeployTarget,
        CancellationToken cancellationToken)
    {
        if (websiteTarget is null || websiteTarget.Status != DeploymentStatuses.Success)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(websiteTarget.DeployUrl))
        {
            var websiteUrl = CrossProviderUrlWiring.NormalizeOrigin(websiteTarget.DeployUrl);
            if (websiteDeployTarget?.Credential is null)
            {
                return websiteUrl;
            }

            return await ResolveWebsiteUrlAsync(
                websiteDeployTarget,
                websiteTarget.DeployUrl,
                websiteUrl,
                cancellationToken) ?? websiteUrl;
        }

        var storedUrl = await TryResolveLatestStoredDeployUrlAsync(websiteTarget.DeployTargetId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(storedUrl))
        {
            return CrossProviderUrlWiring.NormalizeOrigin(storedUrl);
        }

        if (websiteDeployTarget?.Credential is null || string.IsNullOrWhiteSpace(websiteDeployTarget.ProviderProjectId))
        {
            return null;
        }

        // The target's own provider, not a hardcoded name — asking Railway about a Coolify
        // uuid threw "missing setup details" and failed an otherwise-successful deployment.
        var liveUrl = await ResolveLiveProviderUrlAsync(
            websiteDeployTarget.ProviderName,
            websiteDeployTarget.Credential,
            websiteDeployTarget.ProviderProjectId,
            cancellationToken);
        return string.IsNullOrWhiteSpace(liveUrl) ? null : CrossProviderUrlWiring.NormalizeOrigin(liveUrl);
    }

    private async Task<string?> TryResolveApiUrlAsync(
        DeploymentTarget? serverTarget,
        DeployTarget? serverDeployTarget,
        CancellationToken cancellationToken)
    {
        if (serverTarget is null || serverTarget.Status != DeploymentStatuses.Success)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(serverTarget.DeployUrl))
        {
            return CrossProviderUrlWiring.NormalizeOrigin(serverTarget.DeployUrl);
        }

        var storedUrl = await TryResolveLatestStoredDeployUrlAsync(serverTarget.DeployTargetId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(storedUrl))
        {
            return CrossProviderUrlWiring.NormalizeOrigin(storedUrl);
        }

        if (serverDeployTarget?.Credential is null || string.IsNullOrWhiteSpace(serverDeployTarget.ProviderProjectId))
        {
            return null;
        }

        var liveUrl = await ResolveLiveProviderUrlAsync(
            serverDeployTarget.ProviderName,
            serverDeployTarget.Credential,
            serverDeployTarget.ProviderProjectId,
            cancellationToken);
        return string.IsNullOrWhiteSpace(liveUrl) ? null : CrossProviderUrlWiring.NormalizeOrigin(liveUrl);
    }

    private async Task<string?> TryResolveLatestStoredDeployUrlAsync(
        Guid deployTargetId,
        CancellationToken cancellationToken) =>
        await _db.DeploymentTargets
            .Where(target =>
                target.DeployTargetId == deployTargetId &&
                target.Status == DeploymentStatuses.Success &&
                !string.IsNullOrWhiteSpace(target.DeployUrl))
            .OrderByDescending(target => target.CompletedAt)
            .Select(target => target.DeployUrl)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<string?> ResolveLiveProviderUrlAsync(
        string providerName,
        ProviderCredential credential,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var operations = _serviceOperationsFactory.GetServiceOperations(providerName);
        if (operations is null)
        {
            return null;
        }

        var credentials = new ProviderCredentials(await _tokens.GetTokenAsync(credential, cancellationToken));
        var status = await operations.GetServiceStatusAsync(credentials, providerProjectId, cancellationToken);
        return status.DeployUrl;
    }

    private static string BuildMissingConnectionUrlMessage(string? websiteUrl, string? apiUrl)
    {
        var missingWebsite = string.IsNullOrWhiteSpace(websiteUrl);
        var missingApi = string.IsNullOrWhiteSpace(apiUrl);

        if (missingWebsite && missingApi)
        {
            return "Connection checks require public URLs for both website and server.";
        }

        if (missingWebsite)
        {
            return "Connection checks require a public website URL. Redeploy the website or reconnect Vercel so DeployAI can read the live URL.";
        }

        return "Connection checks require a public API URL. Redeploy the API or reconnect Railway so DeployAI can read the live service URL.";
    }

    private static DeployTarget? ResolveWebsiteDeployTarget(Deployment deployment, DeploymentTarget? websiteTarget) =>
        websiteTarget is not null
            ? deployment.Project.DeployTargets.FirstOrDefault(t => t.Id == websiteTarget.DeployTargetId)
            : deployment.Project.DeployTargets.FirstOrDefault(t =>
                string.Equals(t.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase));

    private static DeployTarget? ResolveServerDeployTarget(Deployment deployment, DeploymentTarget? serverTarget) =>
        serverTarget is not null
            ? deployment.Project.DeployTargets.FirstOrDefault(t => t.Id == serverTarget.DeployTargetId)
            : deployment.Project.DeployTargets.FirstOrDefault(t =>
                string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase));

    private static DeploymentVerificationResult EmptyResult(DeploymentVerificationScope scope, string message) =>
        new(
            false,
            scope.ToString().ToLowerInvariant(),
            [Failed("deployment.missing", "connection", "Deployment", null, message, null, coolifyFullStack: false)],
            DateTimeOffset.UtcNow);

    private static string MapStatus(ProbeCheckStatus status) =>
        status switch
        {
            ProbeCheckStatus.Passed => "passed",
            ProbeCheckStatus.Warning => "warning",
            ProbeCheckStatus.Skipped => "skipped",
            _ => "failed"
        };

    private static DeploymentVerificationCheck ToCheck(
        string id,
        string target,
        string label,
        string? url,
        ProbeCheckResult result,
        bool coolifyFullStack)
    {
        var status = MapStatus(result.Status);
        return new DeploymentVerificationCheck(
            id,
            target,
            label,
            status,
            result.Message,
            url,
            result.SuggestedAction,
            DeploymentVerificationCheckMetadata.CanRequestClaudeFix(status, result.SuggestedAction),
            DeploymentVerificationCheckMetadata.ResolveReferencedFiles(id, result.SuggestedAction, coolifyFullStack));
    }

    private static DeploymentVerificationCheck Skipped(
        string id,
        string target,
        string label,
        string message) =>
        new(id, target, label, "skipped", message, null, null, false, []);

    private static DeploymentVerificationCheck Failed(
        string id,
        string target,
        string label,
        string? url,
        string message,
        string? suggestedAction,
        bool coolifyFullStack)
    {
        var status = "failed";
        return new DeploymentVerificationCheck(
            id,
            target,
            label,
            status,
            message,
            url,
            suggestedAction,
            DeploymentVerificationCheckMetadata.CanRequestClaudeFix(status, suggestedAction),
            DeploymentVerificationCheckMetadata.ResolveReferencedFiles(id, suggestedAction, coolifyFullStack));
    }
}
