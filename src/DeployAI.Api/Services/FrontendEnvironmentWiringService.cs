using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DeployAI.Api.Services;

/// <summary>
/// Wires CORS/API-URL environment variables between a project's website and server deploy targets
/// (both at deploy time, per-target, and on demand via <see cref="SyncCrossProviderEnvironmentAsync"/>),
/// and verifies the wiring actually took effect.
/// </summary>
public interface IFrontendEnvironmentWiringService
{
    /// <summary>Runs a full cross-provider environment sync for a project - resolves live URLs, applies env vars, and verifies. See the implementation for full behavior.</summary>
    Task<EnvironmentSyncResult> SyncCrossProviderEnvironmentAsync(
        Guid projectId,
        EnvironmentSyncOptions options,
        CancellationToken cancellationToken);

    /// <summary>Called before deploying a website target: applies the API URL env var so the build bakes in the correct backend origin. Returns a commit SHA if a repo file (e.g. vercel.json) had to be updated first.</summary>
    Task<string?> WireWebsiteTargetBeforeDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken);

    /// <summary>Called before deploying a Railway server target: applies CORS/frontend-URL env vars so the server accepts requests from the website once it comes up.</summary>
    Task WireServerTargetBeforeRailwayDeployAsync(
        Guid deploymentId,
        DeploymentTarget railwayTarget,
        CancellationToken cancellationToken);

    /// <summary>Called after a website target deploys successfully: re-wires the server side now that the website's final URL is known, and verifies the pair.</summary>
    Task WireServerTargetAfterWebsiteDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken);

    /// <summary>Re-applies runtime env vars to a Railway server target outside the normal deploy flow, optionally redeploying it afterward.</summary>
    Task SyncRailwayServerRuntimeEnvAsync(
        Guid projectId,
        Guid serverDeployTargetId,
        bool redeployAfterUpdate,
        CancellationToken cancellationToken);

    /// <summary>Probes the deployment's website/API endpoints to confirm the wiring actually works, returning human-readable pass/fail messages.</summary>
    Task<IReadOnlyList<string>> VerifyWiredEndpointsAsync(
        Guid deploymentId,
        CancellationToken cancellationToken);
}

public sealed class FrontendEnvironmentWiringService : IFrontendEnvironmentWiringService
{
    private readonly DeployAIDbContext _db;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IProviderServiceOperationsFactory _serviceOperationsFactory;
    private readonly IProviderFactory _providerFactory;
    private readonly IProviderApplicationUrlResolverFactory _applicationUrlResolverFactory;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IGitHubService _gitHubService;
    private readonly IEncryptionService _encryption;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDeploymentReadinessService _deploymentReadiness;
    private readonly IDeploymentVerificationService _deploymentVerification;

    public FrontendEnvironmentWiringService(
        DeployAIDbContext db,
        IProviderManagementFactory managementFactory,
        IProviderServiceOperationsFactory serviceOperationsFactory,
        IProviderFactory providerFactory,
        IProviderApplicationUrlResolverFactory applicationUrlResolverFactory,
        IProviderCredentialTokenService tokens,
        IGitHubService gitHubService,
        IEncryptionService encryption,
        IHttpClientFactory httpClientFactory,
        IDeploymentReadinessService deploymentReadiness,
        IDeploymentVerificationService deploymentVerification)
    {
        _db = db;
        _managementFactory = managementFactory;
        _serviceOperationsFactory = serviceOperationsFactory;
        _providerFactory = providerFactory;
        _applicationUrlResolverFactory = applicationUrlResolverFactory;
        _tokens = tokens;
        _gitHubService = gitHubService;
        _encryption = encryption;
        _httpClientFactory = httpClientFactory;
        _deploymentReadiness = deploymentReadiness;
        _deploymentVerification = deploymentVerification;
    }

    public async Task<EnvironmentSyncResult> SyncCrossProviderEnvironmentAsync(
        Guid projectId,
        EnvironmentSyncOptions options,
        CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .ThenInclude(t => t.Credential)
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        if (project is null)
        {
            return SkippedResult(options.Source, completedAt, "Project not found.");
        }

        // Resolve every valid (website, server) pair - not just the first one - so a project
        // with both a Vercel+Railway pair and a Coolify+Coolify pair gets both synced instead of
        // silently starving whichever pair doesn't happen to be matched first.
        var pairs = DeploymentTargetResolution.ResolveProviderPairs(project.DeployTargets)
            .Where(pair => pair.Website.Credential is not null && pair.Server.Credential is not null)
            .ToList();

        if (pairs.Count == 0)
        {
            return SkippedResult(
                options.Source,
                completedAt,
                "Project does not have a supported website and server provider pair.");
        }

        var pairResults = new List<EnvironmentSyncResult>();
        foreach (var pair in pairs)
        {
            pairResults.Add(await SyncProviderPairAsync(
                project,
                pair.Website,
                pair.Server,
                options,
                completedAt,
                cancellationToken));
        }

        var result = CombineResults(pairResults, options, completedAt);
        await PersistSyncStateAsync(project, result, cancellationToken);
        return result;
    }

    private async Task<EnvironmentSyncResult> SyncProviderPairAsync(
        Project project,
        DeployTarget websiteDeployTarget,
        DeployTarget serverDeployTarget,
        EnvironmentSyncOptions options,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var websiteConfig = DeployTargetConfig.Parse(websiteDeployTarget.ConfigJson);
        var serverConfig = DeployTargetConfig.Parse(serverDeployTarget.ConfigJson);
        var apiUrl = await ResolveLiveApiUrlAsync(serverDeployTarget, cancellationToken);
        var websiteUrl = await ResolveKnownPublicWebsiteUrlAsync(
            websiteDeployTarget,
            deploymentId: null,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(websiteUrl))
        {
            return SkippedResult(
                options.Source,
                completedAt,
                "Could not resolve live API URL and website URL.");
        }

        apiUrl = CrossProviderUrlWiring.NormalizeOrigin(apiUrl);
        websiteUrl = CrossProviderUrlWiring.NormalizeOrigin(websiteUrl);
        var websiteOrigins = await ResolveWebsiteOriginsAsync(websiteDeployTarget, websiteUrl, cancellationToken);

        var driftDetails = await DetectEnvironmentDriftAsync(
            websiteDeployTarget,
            serverDeployTarget,
            websiteConfig.Framework,
            serverConfig.Framework,
            websiteUrl,
            websiteOrigins,
            apiUrl,
            cancellationToken);

        var usesSplitOrigin = CrossProviderUrlWiring.ShouldUseSplitOrigin(
            websiteConfig.Framework,
            serverConfig.Framework);

        if (options.DetectDriftOnly)
        {
            if (usesSplitOrigin)
            {
                var staleBuildDrift = await DetectStaleSplitOriginBuildDriftAsync(
                    project.Id,
                    websiteUrl,
                    apiUrl,
                    cancellationToken);
                if (staleBuildDrift is not null)
                {
                    driftDetails = [.. driftDetails, staleBuildDrift];
                }
            }

            return new EnvironmentSyncResult(
                Success: driftDetails.Count == 0,
                DriftDetected: driftDetails.Count > 0,
                Skipped: false,
                SkipReason: null,
                ResolvedWebsiteUrl: websiteUrl,
                ResolvedApiUrl: apiUrl,
                RailwayKeysApplied: [],
                VercelKeysApplied: [],
                VerificationMessages: [],
                DriftDetails: driftDetails,
                Source: options.Source,
                CompletedAt: completedAt);
        }

        var hadWebsiteDrift = driftDetails.Any(d =>
            d.StartsWith(GetWebsiteDriftLabel(websiteDeployTarget.ProviderName), StringComparison.Ordinal));

        var railwayKeysApplied = new List<string>();
        var vercelKeysApplied = new List<string>();

        if (options.ApplyVercelEnv)
        {
            if (string.Equals(websiteDeployTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
            {
                vercelKeysApplied.AddRange(await ApplyCoolifyApiEnvironmentAsync(
                    websiteDeployTarget,
                    websiteConfig.Framework,
                    apiUrl,
                    cancellationToken));
            }
            else
            {
                vercelKeysApplied.AddRange(await ApplyVercelApiEnvironmentAsync(
                    websiteDeployTarget,
                    websiteConfig.Framework,
                    apiUrl,
                    cancellationToken));
            }
        }

        var isCoolifyStack =
            string.Equals(websiteDeployTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(serverDeployTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);

        var vercelRedeployTriggered = false;
        var coolifyWebsiteRedeployTriggered = false;
        string? vercelCommitSha = null;
        string? domainAssignmentMessage = null;
        if (options.EnsureWebsiteWiring && !isCoolifyStack)
        {
            vercelCommitSha = await EnsureWebsiteWiringAsync(
                project,
                websiteDeployTarget,
                websiteConfig,
                serverConfig.Framework,
                apiUrl,
                cancellationToken);
        }

        var shouldRedeployWebsite = options.RedeployVercelAfterUpdate ||
            !string.IsNullOrWhiteSpace(vercelCommitSha) ||
            hadWebsiteDrift;

        if (options.EnsureWebsiteWiring &&
            !usesSplitOrigin &&
            CrossProviderUrlWiring.UsesRelativeApiPaths(websiteConfig.Framework) &&
            !shouldRedeployWebsite &&
            !isCoolifyStack)
        {
            var proxyWorking = await ProbeProxiedApiPostAsync(websiteUrl, cancellationToken);
            if (proxyWorking == false)
            {
                shouldRedeployWebsite = true;
            }
        }

        string? deployedSpaWiringMessage = null;
        if (options.EnsureWebsiteWiring &&
            usesSplitOrigin &&
            !shouldRedeployWebsite)
        {
            var spaWired = await ProbeDeployedSpaWiredToApiAsync(websiteUrl, apiUrl, cancellationToken);
            if (spaWired == false)
            {
                var blockingIssues = await TryGetSplitOriginBlockingIssuesAsync(project.Id, cancellationToken);
                if (blockingIssues is null || blockingIssues.Count == 0)
                {
                    // The repo has the split-origin wiring but the deployed bundle predates the
                    // env vars, so relative /api calls still miss the API host.
                    // Only a production rebuild bakes the API URL into the SPA.
                    shouldRedeployWebsite = true;
                }
                else
                {
                    var websiteHostLabel = isCoolifyStack ? "Coolify static host" : "Vercel static host";
                    var apiHostLabel = isCoolifyStack ? "Coolify API" : "Railway API";
                    deployedSpaWiringMessage =
                        $"Deployed SPA wiring check failed: the deployed site does not call the {apiHostLabel} directly, " +
                        $"so its /api requests hit the {websiteHostLabel} and return 405. Missing split-origin setup files: " +
                        string.Join("; ", blockingIssues.Select(issue => $"{issue.Path} ({issue.Reason})")) +
                        ". Regenerate the deployment setup files, merge them, then deploy and sync again.";
                }
            }
            else if (spaWired == true)
            {
                if (isCoolifyStack)
                {
                    deployedSpaWiringMessage =
                        "Deployed SPA wiring check passed: interceptor and apiBaseUrl are baked into the production bundle.";
                }
                else
                {
                    var railwayAuth = await ProbeRailwayAuthEndpointAsync(apiUrl, cancellationToken);
                    deployedSpaWiringMessage = railwayAuth == false
                        ? $"Deployed SPA bundle wiring passed, but Railway POST /api/v1/auth/login returned 405 at {apiUrl}."
                        : "Deployed SPA wiring check passed: interceptor and apiBaseUrl are baked into the production bundle.";
                }
            }
        }

        if (shouldRedeployWebsite && isCoolifyStack)
        {
            coolifyWebsiteRedeployTriggered = await TriggerCoolifyWebsiteRedeployAsync(
                websiteDeployTarget,
                cancellationToken);
        }
        else if (shouldRedeployWebsite && !isCoolifyStack)
        {
            var triggeredDeploymentId = await TriggerVercelProductionRedeployAsync(
                project,
                websiteDeployTarget,
                websiteConfig,
                vercelCommitSha,
                cancellationToken);
            vercelRedeployTriggered = !string.IsNullOrWhiteSpace(triggeredDeploymentId);
        }
        else if (options.EnsureWebsiteWiring && !isCoolifyStack)
        {
            domainAssignmentMessage = await EnsureVercelProductionDomainsAsync(
                websiteDeployTarget,
                deploymentId: null,
                cancellationToken);
        }

        if (options.ApplyRailwayEnv)
        {
            if (string.Equals(serverDeployTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
            {
                railwayKeysApplied.AddRange(await ApplyCoolifyServerEnvironmentAsync(
                    serverDeployTarget,
                    serverConfig.Framework,
                    websiteConfig.Framework,
                    websiteUrl,
                    websiteOrigins,
                    apiUrl,
                    cancellationToken));
            }
            else
            {
                railwayKeysApplied.AddRange(await ApplyRailwayServerEnvironmentAsync(
                    serverDeployTarget,
                    serverConfig.Framework,
                    websiteConfig.Framework,
                    websiteUrl,
                    websiteOrigins,
                    apiUrl,
                    cancellationToken));
            }
        }

        if (options.RedeployRailwayAfterUpdate)
        {
            var serviceOperations = _serviceOperationsFactory.GetServiceOperations(serverDeployTarget.ProviderName);
            if (serviceOperations is not null)
            {
                var serverCredentials = await GetCredentialsAsync(serverDeployTarget, cancellationToken);
                await serviceOperations.RedeployServiceAsync(
                    serverCredentials,
                    serverDeployTarget.ProviderProjectId,
                    cancellationToken);
            }
        }

        driftDetails = await DetectEnvironmentDriftAsync(
            websiteDeployTarget,
            serverDeployTarget,
            websiteConfig.Framework,
            serverConfig.Framework,
            websiteUrl,
            websiteOrigins,
            apiUrl,
            cancellationToken);

        IReadOnlyList<string> verificationMessages = [];
        if (options.RunVerification)
        {
            if (vercelRedeployTriggered)
            {
                verificationMessages =
                [
                    "Vercel production redeploy triggered. Wait for the deployment to finish, then sync again to verify split-origin wiring and CORS."
                ];
            }
            else if (coolifyWebsiteRedeployTriggered)
            {
                verificationMessages =
                [
                    "Coolify website redeploy triggered. Wait for the deployment to finish, then sync again to verify split-origin wiring and CORS."
                ];
            }
            else if (!string.IsNullOrWhiteSpace(domainAssignmentMessage))
            {
                verificationMessages = [domainAssignmentMessage];
            }
            else
            {
                verificationMessages = await VerifyWiredEndpointsForUrlsAsync(
                    websiteUrl,
                    apiUrl,
                    websiteConfig.Framework,
                    serverConfig.Framework,
                    cancellationToken);
            }

            if (!vercelRedeployTriggered &&
                !coolifyWebsiteRedeployTriggered &&
                deployedSpaWiringMessage is not null)
            {
                verificationMessages = [.. verificationMessages, deployedSpaWiringMessage];
            }
        }

        return new EnvironmentSyncResult(
            Success: verificationMessages.All(message => message.Contains("passed", StringComparison.OrdinalIgnoreCase)),
            DriftDetected: driftDetails.Count > 0,
            Skipped: false,
            SkipReason: null,
            ResolvedWebsiteUrl: websiteUrl,
            ResolvedApiUrl: apiUrl,
            RailwayKeysApplied: railwayKeysApplied,
            VercelKeysApplied: vercelKeysApplied,
            VerificationMessages: verificationMessages,
            DriftDetails: driftDetails,
            Source: options.Source,
            CompletedAt: completedAt);
    }

    private static EnvironmentSyncResult CombineResults(
        IReadOnlyList<EnvironmentSyncResult> results,
        EnvironmentSyncOptions options,
        DateTimeOffset completedAt)
    {
        if (results.Count == 1)
        {
            return results[0];
        }

        return new EnvironmentSyncResult(
            Success: results.All(r => r.Success),
            DriftDetected: results.Any(r => r.DriftDetected),
            Skipped: false,
            SkipReason: null,
            ResolvedWebsiteUrl: results[0].ResolvedWebsiteUrl,
            ResolvedApiUrl: results[0].ResolvedApiUrl,
            RailwayKeysApplied: results.SelectMany(r => r.RailwayKeysApplied).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            VercelKeysApplied: results.SelectMany(r => r.VercelKeysApplied).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            VerificationMessages: results.SelectMany(r => r.VerificationMessages).ToList(),
            DriftDetails: results.SelectMany(r => r.DriftDetails).ToList(),
            Source: options.Source,
            CompletedAt: completedAt);
    }

    public async Task<string?> WireWebsiteTargetBeforeDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken)
    {
        if (!IsWebsiteProvider(websiteTarget.ProviderName))
        {
            return null;
        }

        var context = await LoadDualTargetContextAsync(deploymentId, websiteTarget, cancellationToken);
        if (context is null || string.IsNullOrWhiteSpace(context.ApiUrl))
        {
            return null;
        }

        var websiteConfig = DeployTargetConfig.Parse(context.WebsiteDeployTarget!.ConfigJson);
        var serverConfig = DeployTargetConfig.Parse(context.ServerDeployTarget.ConfigJson);
        var normalizedApiUrl = CrossProviderUrlWiring.NormalizeOrigin(context.ApiUrl);

        if (string.Equals(websiteTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
        {
            await ApplyCoolifyApiEnvironmentAsync(
                context.WebsiteDeployTarget,
                websiteConfig.Framework,
                normalizedApiUrl,
                cancellationToken);
            return null;
        }

        await ApplyVercelApiEnvironmentAsync(
            context.WebsiteDeployTarget,
            websiteConfig.Framework,
            normalizedApiUrl,
            cancellationToken);

        return await EnsureWebsiteWiringAsync(
            context.Project,
            context.WebsiteDeployTarget,
            websiteConfig,
            serverConfig.Framework,
            normalizedApiUrl,
            cancellationToken);
    }

    public async Task WireServerTargetBeforeRailwayDeployAsync(
        Guid deploymentId,
        DeploymentTarget railwayTarget,
        CancellationToken cancellationToken)
    {
        if (!IsServerProvider(railwayTarget.ProviderName))
        {
            return;
        }

        var deployment = await _db.Deployments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deploymentId, cancellationToken);
        if (deployment is null)
        {
            return;
        }

        await SyncCrossProviderEnvironmentAsync(
            deployment.ProjectId,
            new EnvironmentSyncOptions(
                RedeployRailwayAfterUpdate: false,
                EnsureWebsiteWiring: false,
                ApplyVercelEnv: false,
                ApplyRailwayEnv: true,
                RunVerification: false,
                Source: "deploy"),
            cancellationToken);
    }

    public async Task WireServerTargetAfterWebsiteDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken)
    {
        if (!IsWebsiteProvider(websiteTarget.ProviderName) ||
            websiteTarget.Status != DeploymentStatuses.Success ||
            string.IsNullOrWhiteSpace(websiteTarget.DeployUrl))
        {
            return;
        }

        var deployment = await _db.Deployments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deploymentId, cancellationToken);
        if (deployment is null)
        {
            return;
        }

        await SyncCrossProviderEnvironmentAsync(
            deployment.ProjectId,
            new EnvironmentSyncOptions(
                RedeployRailwayAfterUpdate: true,
                EnsureWebsiteWiring: true,
                ApplyVercelEnv: true,
                ApplyRailwayEnv: true,
                RunVerification: true,
                Source: "deploy"),
            cancellationToken);
    }

    public Task SyncRailwayServerRuntimeEnvAsync(
        Guid projectId,
        Guid serverDeployTargetId,
        bool redeployAfterUpdate,
        CancellationToken cancellationToken) =>
        SyncCrossProviderEnvironmentAsync(
            projectId,
            new EnvironmentSyncOptions(
                RedeployRailwayAfterUpdate: redeployAfterUpdate,
                EnsureWebsiteWiring: true,
                ApplyVercelEnv: true,
                ApplyRailwayEnv: true,
                RunVerification: false,
                Source: "manual"),
            cancellationToken);

    public async Task<IReadOnlyList<string>> VerifyWiredEndpointsAsync(
        Guid deploymentId,
        CancellationToken cancellationToken)
    {
        var result = await _deploymentVerification.VerifyAsync(
            deploymentId,
            DeploymentVerificationScope.Both,
            cancellationToken);

        return result.Checks
            .Where(check => check.Target is "server" or "connection")
            .Where(check => check.Status is not "skipped")
            .Select(check => check.Status switch
            {
                "passed" => $"{check.Label} check passed: {check.Message}",
                "warning" => $"{check.Label} check warning: {check.Message}",
                _ => $"{check.Label} check failed: {check.Message}"
            })
            .ToList();
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

    private async Task<ServerWiringContext?> LoadDualTargetContextAsync(
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
                IsServerProvider(t.ProviderName) &&
                !string.IsNullOrWhiteSpace(t.DeployUrl));

        var serverDeployTarget = serverDeploymentTarget is null
            ? deployment.Project.DeployTargets.FirstOrDefault(t =>
                IsServerProvider(t.ProviderName) &&
                string.Equals(DeployTargetConfig.Parse(t.ConfigJson).Role, "server", StringComparison.OrdinalIgnoreCase))
            : deployment.Project.DeployTargets.FirstOrDefault(t => t.Id == serverDeploymentTarget.DeployTargetId);

        var apiUrl = serverDeploymentTarget?.DeployUrl;
        if (string.IsNullOrWhiteSpace(apiUrl) && serverDeployTarget?.Credential is not null)
        {
            apiUrl = await ResolveLiveApiUrlAsync(serverDeployTarget, cancellationToken);
        }

        if (serverDeployTarget is null)
        {
            return null;
        }

        return new ServerWiringContext(
            deployment.Project,
            websiteDeployTarget,
            serverDeployTarget,
            apiUrl,
            null);
    }

    private async Task<string?> ResolveLiveApiUrlAsync(
        DeployTarget serverDeployTarget,
        CancellationToken cancellationToken)
    {
        if (string.Equals(serverDeployTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
        {
            if (_applicationUrlResolverFactory.GetResolver(ProviderNameValues.Coolify) is not { } resolver)
            {
                return null;
            }

            var credentials = await GetCredentialsAsync(serverDeployTarget, cancellationToken);
            return await resolver.ResolveApplicationUrlAsync(
                credentials,
                serverDeployTarget.ProviderProjectId,
                cancellationToken);
        }

        if (_serviceOperationsFactory.GetServiceOperations("railway") is not { } railwayOperations)
        {
            return null;
        }

        var serverCredentials = await GetCredentialsAsync(serverDeployTarget, cancellationToken);
        var status = await railwayOperations.GetServiceStatusAsync(
            serverCredentials,
            serverDeployTarget.ProviderProjectId,
            cancellationToken);
        return status?.DeployUrl;
    }

    private async Task<string?> ResolveKnownPublicWebsiteUrlAsync(
        DeployTarget? websiteDeployTarget,
        Guid? deploymentId,
        CancellationToken cancellationToken)
    {
        if (websiteDeployTarget?.Credential is null)
        {
            return null;
        }

        if (string.Equals(websiteDeployTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
        {
            string? deploymentUrl = null;
            if (deploymentId.HasValue)
            {
                deploymentUrl = await _db.DeploymentTargets
                    .Where(t =>
                        t.DeploymentId == deploymentId.Value &&
                        t.DeployTargetId == websiteDeployTarget.Id &&
                        !string.IsNullOrWhiteSpace(t.DeployUrl))
                    .OrderByDescending(t => t.CompletedAt)
                    .Select(t => t.DeployUrl)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            deploymentUrl ??= await _db.DeploymentTargets
                .Where(t =>
                    t.DeployTargetId == websiteDeployTarget.Id &&
                    t.Status == DeploymentStatuses.Success &&
                    !string.IsNullOrWhiteSpace(t.DeployUrl))
                .OrderByDescending(t => t.CompletedAt)
                .Select(t => t.DeployUrl)
                .FirstOrDefaultAsync(cancellationToken);

            return await ResolveCoolifyPublicWebsiteUrlAsync(
                websiteDeployTarget,
                deploymentUrl,
                cancellationToken);
        }

        if (deploymentId.HasValue)
        {
            var currentWebsiteTarget = await _db.DeploymentTargets
                .Where(t =>
                    t.DeploymentId == deploymentId.Value &&
                    t.DeployTargetId == websiteDeployTarget.Id &&
                    !string.IsNullOrWhiteSpace(t.DeployUrl))
                .OrderByDescending(t => t.CompletedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(currentWebsiteTarget?.DeployUrl))
            {
                return await ResolveVercelPublicWebsiteUrlAsync(
                    websiteDeployTarget,
                    currentWebsiteTarget.DeployUrl,
                    cancellationToken);
            }
        }

        var lastWebsiteUrl = await _db.DeploymentTargets
            .Where(t =>
                t.DeployTargetId == websiteDeployTarget.Id &&
                t.Status == DeploymentStatuses.Success &&
                !string.IsNullOrWhiteSpace(t.DeployUrl))
            .OrderByDescending(t => t.CompletedAt)
            .Select(t => t.DeployUrl)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(lastWebsiteUrl))
        {
            return await ResolveVercelPublicWebsiteUrlAsync(
                websiteDeployTarget,
                lastWebsiteUrl,
                cancellationToken);
        }

        return await ResolveVercelPublicWebsiteUrlAsync(
            websiteDeployTarget,
            deploymentUrl: null,
            cancellationToken);
    }

    private async Task<string?> ResolveCoolifyPublicWebsiteUrlAsync(
        DeployTarget websiteDeployTarget,
        string? deploymentUrl,
        CancellationToken cancellationToken)
    {
        if (_applicationUrlResolverFactory.GetResolver(ProviderNameValues.Coolify) is { } resolver)
        {
            var credentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
            var resolved = await resolver.ResolveApplicationUrlAsync(
                credentials,
                websiteDeployTarget.ProviderProjectId,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return CrossProviderUrlWiring.NormalizeOrigin(resolved);
            }
        }

        return string.IsNullOrWhiteSpace(deploymentUrl)
            ? null
            : CrossProviderUrlWiring.NormalizeOrigin(deploymentUrl);
    }

    private async Task<string?> ResolveVercelPublicWebsiteUrlAsync(
        DeployTarget websiteDeployTarget,
        string? deploymentUrl,
        CancellationToken cancellationToken)
    {
        var vercelManagement = _managementFactory.GetManagement("vercel");
        if (vercelManagement is IWebsiteApiProxySupport proxySupport)
        {
            var credentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
            var resolved = await proxySupport.ResolvePublicWebsiteUrlAsync(
                credentials,
                websiteDeployTarget.ProviderProjectId,
                deploymentUrl,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return CrossProviderUrlWiring.NormalizeOrigin(resolved);
            }
        }

        return string.IsNullOrWhiteSpace(deploymentUrl)
            ? null
            : CrossProviderUrlWiring.NormalizeOrigin(deploymentUrl);
    }

    private async Task<IReadOnlyList<string>> ResolveWebsiteOriginsAsync(
        DeployTarget websiteDeployTarget,
        string primaryWebsiteUrl,
        CancellationToken cancellationToken)
    {
        if (string.Equals(websiteDeployTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
        {
            return [primaryWebsiteUrl];
        }

        var vercelManagement = _managementFactory.GetManagement("vercel");
        if (vercelManagement is IWebsiteApiProxySupport proxySupport)
        {
            var credentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
            var origins = await proxySupport.ListProductionWebsiteOriginsAsync(
                credentials,
                websiteDeployTarget.ProviderProjectId,
                cancellationToken);
            if (origins is { Count: > 0 })
            {
                return origins;
            }
        }

        return [primaryWebsiteUrl];
    }

    private static bool IsWebsiteProvider(string providerName) =>
        string.Equals(providerName, "vercel", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);

    private static bool IsServerProvider(string providerName) =>
        string.Equals(providerName, "railway", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<string>> ApplyCoolifyApiEnvironmentAsync(
        DeployTarget websiteDeployTarget,
        string? websiteFramework,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        var appliedKeys = new List<string>();
        var management = _managementFactory.GetManagement(ProviderNameValues.Coolify);
        var credentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
        var normalizedApiUrl = CrossProviderUrlWiring.NormalizeOrigin(apiUrl);

        foreach (var key in CrossProviderUrlWiring.ResolveApiEnvKeys(websiteFramework))
        {
            await management.UpsertEnvVarAsync(
                credentials,
                websiteDeployTarget.ProviderProjectId,
                new UpsertProviderEnvVarRequest(key, normalizedApiUrl, "plain", []),
                cancellationToken);
            appliedKeys.Add(key);
        }

        return appliedKeys;
    }

    private async Task<IReadOnlyList<string>> ApplyCoolifyServerEnvironmentAsync(
        DeployTarget serverDeployTarget,
        string? serverFramework,
        string? websiteFramework,
        string websiteUrl,
        IReadOnlyList<string> websiteOrigins,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        var appliedKeys = new List<string>();
        var coolifyManagement = _managementFactory.GetManagement(ProviderNameValues.Coolify);
        var serverCredentials = await GetCredentialsAsync(serverDeployTarget, cancellationToken);
        var assignments = CrossProviderUrlWiring.BuildServerRuntimeEnvAssignments(
            serverFramework,
            websiteFramework,
            websiteUrl,
            websiteOrigins,
            apiUrl);

        foreach (var (key, value) in assignments)
        {
            await coolifyManagement.UpsertEnvVarAsync(
                serverCredentials,
                serverDeployTarget.ProviderProjectId,
                new UpsertProviderEnvVarRequest(key, value, "plain", []),
                cancellationToken);
            appliedKeys.Add(key);
        }

        return appliedKeys;
    }

    private async Task<IReadOnlyList<string>> ApplyVercelApiEnvironmentAsync(
        DeployTarget websiteDeployTarget,
        string? websiteFramework,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        var appliedKeys = new List<string>();
        var management = _managementFactory.GetManagement("vercel");
        var credentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
        var normalizedApiUrl = CrossProviderUrlWiring.NormalizeOrigin(apiUrl);

        foreach (var key in CrossProviderUrlWiring.ResolveApiEnvKeys(websiteFramework))
        {
            await management.UpsertEnvVarAsync(
                credentials,
                websiteDeployTarget.ProviderProjectId,
                new UpsertProviderEnvVarRequest(
                    key,
                    normalizedApiUrl,
                    "encrypted",
                    ["production", "preview", "development"]),
                cancellationToken);
            appliedKeys.Add(key);
        }

        return appliedKeys;
    }

    private async Task<IReadOnlyList<string>> ApplyRailwayServerEnvironmentAsync(
        DeployTarget serverDeployTarget,
        string? serverFramework,
        string? websiteFramework,
        string websiteUrl,
        IReadOnlyList<string> websiteOrigins,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        var appliedKeys = new List<string>();
        var railwayManagement = _managementFactory.GetManagement("railway");
        var serverCredentials = await GetCredentialsAsync(serverDeployTarget, cancellationToken);
        var assignments = CrossProviderUrlWiring.BuildServerRuntimeEnvAssignments(
            serverFramework,
            websiteFramework,
            websiteUrl,
            websiteOrigins,
            apiUrl);

        foreach (var assignment in assignments)
        {
            await railwayManagement.UpsertEnvVarAsync(
                serverCredentials,
                serverDeployTarget.ProviderProjectId,
                new UpsertProviderEnvVarRequest(
                    assignment.Key,
                    assignment.Value,
                    "plain",
                    []),
                cancellationToken);
            appliedKeys.Add(assignment.Key);
        }

        return appliedKeys;
    }

    private async Task<IReadOnlyList<string>> DetectEnvironmentDriftAsync(
        DeployTarget websiteDeployTarget,
        DeployTarget serverDeployTarget,
        string? websiteFramework,
        string? serverFramework,
        string websiteUrl,
        IReadOnlyList<string> websiteOrigins,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        var driftDetails = new List<string>();
        var expectedServer = CrossProviderUrlWiring.BuildExpectedRailwayEnvValues(
            serverFramework,
            websiteFramework,
            websiteUrl,
            websiteOrigins,
            apiUrl);
        var expectedWebsite = CrossProviderUrlWiring.BuildExpectedVercelEnvValues(websiteFramework, apiUrl);

        var websiteManagement = _managementFactory.GetManagement(websiteDeployTarget.ProviderName);
        var serverManagement = _managementFactory.GetManagement(serverDeployTarget.ProviderName);
        var websiteCredentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
        var serverCredentials = await GetCredentialsAsync(serverDeployTarget, cancellationToken);

        var websiteEnvVars = await websiteManagement.ListEnvVarsAsync(
            websiteCredentials,
            websiteDeployTarget.ProviderProjectId,
            cancellationToken);
        var serverEnvVars = await serverManagement.ListEnvVarsAsync(
            serverCredentials,
            serverDeployTarget.ProviderProjectId,
            cancellationToken);

        CompareExpectedEnvVars(
            driftDetails,
            GetWebsiteDriftLabel(websiteDeployTarget.ProviderName),
            expectedWebsite,
            websiteEnvVars,
            skipHiddenValues: true);
        CompareExpectedEnvVars(
            driftDetails,
            GetServerDriftLabel(serverDeployTarget.ProviderName),
            expectedServer,
            serverEnvVars);

        if (CrossProviderUrlWiring.ShouldUseSplitOrigin(websiteFramework, serverFramework) &&
            string.Equals(serverDeployTarget.ProviderName, ProviderNameValues.Railway, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var warning in CrossProviderUrlWiring.ValidateIgnoredRailwayEnvKeys(
                         serverEnvVars.Select(env => new CrossProviderUrlWiring.ProviderEnvVarSnapshot(env.Key, env.Value)).ToArray()))
            {
                driftDetails.Add(warning);
            }
        }

        return driftDetails;
    }

    private static string GetWebsiteDriftLabel(string providerName) =>
        string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase)
            ? "Coolify"
            : "Vercel";

    private static string GetServerDriftLabel(string providerName) =>
        string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase)
            ? "Coolify"
            : "Railway";

    private async Task<bool> TriggerCoolifyWebsiteRedeployAsync(
        DeployTarget websiteDeployTarget,
        CancellationToken cancellationToken)
    {
        var serviceOperations = _serviceOperationsFactory.GetServiceOperations(ProviderNameValues.Coolify);
        if (serviceOperations is null)
        {
            return false;
        }

        var credentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
        await serviceOperations.RedeployServiceAsync(
            credentials,
            websiteDeployTarget.ProviderProjectId,
            cancellationToken);
        return true;
    }

    private static void CompareExpectedEnvVars(
        List<string> driftDetails,
        string providerLabel,
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyList<ProviderEnvVar> actualEnvVars,
        bool skipHiddenValues = false)
    {
        var actualByKey = actualEnvVars.ToDictionary(env => env.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, expectedValue) in expected)
        {
            if (!actualByKey.TryGetValue(key, out var actual))
            {
                driftDetails.Add($"{providerLabel} missing {key}; expected {expectedValue}.");
                continue;
            }

            if (skipHiddenValues && actual.ValueHidden)
            {
                continue;
            }

            if (!CrossProviderUrlWiring.EnvValueMatches(key, actual.Value, expectedValue))
            {
                driftDetails.Add($"{providerLabel} {key} mismatch; expected {expectedValue}.");
            }
        }
    }

    private async Task<IReadOnlyList<string>> VerifyWiredEndpointsForUrlsAsync(
        string websiteUrl,
        string apiUrl,
        string? websiteFramework,
        string? serverFramework,
        CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var client = _httpClientFactory.CreateClient(nameof(FrontendEnvironmentWiringService));
        client.Timeout = TimeSpan.FromSeconds(20);

        await DeploymentEndpointProbes.AppendReachableMessageAsync(
            client,
            $"{apiUrl}/",
            label: "Railway API",
            messages,
            cancellationToken);

        if (CrossProviderUrlWiring.ShouldUseSplitOrigin(websiteFramework, serverFramework))
        {
            await DeploymentEndpointProbes.AppendSplitOriginHealthMessageAsync(
                client,
                $"{apiUrl}/api/v1/health",
                label: "Railway API health",
                messages,
                cancellationToken);
        }
        else if (CrossProviderUrlWiring.UsesRelativeApiPaths(websiteFramework))
        {
            await DeploymentEndpointProbes.AppendProxiedLoginMessageAsync(
                client,
                websiteUrl,
                label: "Vercel website proxy",
                messages,
                cancellationToken);
        }

        await DeploymentEndpointProbes.AppendCorsMessageAsync(
            client,
            $"{apiUrl}/",
            websiteUrl,
            label: "Railway CORS",
            messages,
            cancellationToken);

        return messages;
    }

    private async Task PersistSyncStateAsync(
        Project project,
        EnvironmentSyncResult result,
        CancellationToken cancellationToken)
    {
        project.EnvironmentSyncJson = ProjectEnvironmentSyncState.FromResult(result).ToJson();
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static EnvironmentSyncResult SkippedResult(
        string source,
        DateTimeOffset completedAt,
        string reason) =>
        new(
            Success: false,
            DriftDetected: false,
            Skipped: true,
            SkipReason: reason,
            ResolvedWebsiteUrl: null,
            ResolvedApiUrl: null,
            RailwayKeysApplied: [],
            VercelKeysApplied: [],
            VerificationMessages: [],
            DriftDetails: [],
            Source: source,
            CompletedAt: completedAt);

    private async Task<string?> EnsureWebsiteWiringAsync(
        Project project,
        DeployTarget websiteDeployTarget,
        DeployTargetConfig websiteConfig,
        string? serverFramework,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        if (CrossProviderUrlWiring.ShouldUseSplitOrigin(websiteConfig.Framework, serverFramework))
        {
            return await EnsureSplitOriginVercelJsonAsync(
                project,
                websiteDeployTarget,
                websiteConfig,
                cancellationToken);
        }

        if (!CrossProviderUrlWiring.UsesRelativeApiPaths(websiteConfig.Framework))
        {
            return null;
        }

        var management = _managementFactory.GetManagement("vercel");
        if (management is not IWebsiteApiProxySupport proxySupport)
        {
            return null;
        }

        var credentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
        await proxySupport.EnsureApiProxyRoutesAsync(
            credentials,
            websiteDeployTarget.ProviderProjectId,
            apiUrl,
            cancellationToken);

        return await EnsureVercelJsonProxyRewritesAsync(
            project,
            websiteDeployTarget,
            websiteConfig,
            apiUrl,
            cancellationToken);
    }

    private async Task<string?> EnsureSplitOriginVercelJsonAsync(
        Project project,
        DeployTarget websiteDeployTarget,
        DeployTargetConfig websiteConfig,
        CancellationToken cancellationToken)
    {
        var repoParts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (repoParts.Length != 2)
        {
            return null;
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var gitHubToken = _encryption.Decrypt(user.GitHubTokenEncrypted);
        var branch = string.IsNullOrWhiteSpace(project.DefaultBranch) ? "main" : project.DefaultBranch;
        var candidatePaths = VercelJsonRewrites.CandidatePaths(websiteConfig).ToList();

        foreach (var candidatePath in candidatePaths)
        {
            var metadata = await _gitHubService.GetFileMetadataAsync(
                gitHubToken,
                repoParts[0],
                repoParts[1],
                candidatePath,
                branch,
                cancellationToken);

            if (metadata is null)
            {
                continue;
            }

            if (!VercelJsonRewrites.HasApiProxyAntiPattern(metadata.Content) &&
                !VercelJsonRewrites.NeedsWriteApiEnvBuildCommand(metadata.Content, websiteConfig) &&
                !VercelJsonRewrites.TryBuildSpaOnlyContent(metadata.Content, websiteConfig, out _))
            {
                return null;
            }

            if (!VercelJsonRewrites.TryBuildSpaOnlyContent(metadata.Content, websiteConfig, out var updatedContent))
            {
                return null;
            }

            return await _gitHubService.UpsertFileAsync(
                gitHubToken,
                repoParts[0],
                repoParts[1],
                candidatePath,
                updatedContent,
                "DeployAI: use SPA-only vercel.json for split-origin Railway API",
                branch,
                metadata.Sha,
                cancellationToken);
        }

        var createPath = VercelJsonRewrites.ResolvePrimaryPath(websiteConfig);
        if (!VercelJsonRewrites.TryBuildSpaOnlyContent(null, websiteConfig, out var createdContent))
        {
            return null;
        }

        return await _gitHubService.UpsertFileAsync(
            gitHubToken,
            repoParts[0],
            repoParts[1],
            createPath,
            createdContent,
            "DeployAI: create SPA-only vercel.json for split-origin Railway API",
            branch,
            existingSha: null,
            cancellationToken);
    }

    private async Task<string?> TriggerVercelProductionRedeployAsync(
        Project project,
        DeployTarget websiteDeployTarget,
        DeployTargetConfig websiteConfig,
        string? commitSha,
        CancellationToken cancellationToken)
    {
        var credentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
        var branch = string.IsNullOrWhiteSpace(project.DefaultBranch) ? "main" : project.DefaultBranch;
        var environment = new Dictionary<string, string>
        {
            ["githubRepoFullName"] = project.GitHubRepoFullName
        };
        foreach (var entry in websiteConfig.ToEnvironmentEntries())
        {
            environment[entry.Key] = entry.Value;
        }

        if (!string.IsNullOrWhiteSpace(commitSha))
        {
            environment["commitSha"] = commitSha;
        }

        var deploymentProvider = _providerFactory.GetProvider("vercel");
        if (deploymentProvider is null)
        {
            return null;
        }

        var deployment = await deploymentProvider.TriggerDeploymentAsync(
            credentials,
            websiteDeployTarget.ProviderProjectId,
            branch,
            environment,
            cancellationToken);

        return deployment.DeploymentId;
    }

    private async Task<string?> EnsureVercelProductionDomainsAsync(
        DeployTarget websiteDeployTarget,
        string? deploymentId,
        CancellationToken cancellationToken)
    {
        var management = _managementFactory.GetManagement("vercel");
        if (management is not IWebsiteApiProxySupport proxySupport)
        {
            return null;
        }

        var credentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);
        deploymentId ??= await proxySupport.GetLatestProductionDeploymentIdAsync(
            credentials,
            websiteDeployTarget.ProviderProjectId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(deploymentId))
        {
            return "Vercel production deployment is still building. Wait for it to finish, then sync again.";
        }

        await proxySupport.AssignProductionDomainsToDeploymentAsync(
            credentials,
            websiteDeployTarget.ProviderProjectId,
            deploymentId,
            cancellationToken);

        return null;
    }

    private async Task<string?> EnsureVercelJsonProxyRewritesAsync(
        Project project,
        DeployTarget websiteDeployTarget,
        DeployTargetConfig websiteConfig,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        var repoParts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (repoParts.Length != 2)
        {
            return null;
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var gitHubToken = _encryption.Decrypt(user.GitHubTokenEncrypted);
        var branch = string.IsNullOrWhiteSpace(project.DefaultBranch) ? "main" : project.DefaultBranch;

        var candidatePaths = VercelJsonRewrites.CandidatePaths(websiteConfig).ToList();
        foreach (var candidatePath in candidatePaths)
        {
            var metadata = await _gitHubService.GetFileMetadataAsync(
                gitHubToken,
                repoParts[0],
                repoParts[1],
                candidatePath,
                branch,
                cancellationToken);

            if (metadata is null)
            {
                continue;
            }

            if (!VercelJsonRewrites.TryBuildUpdatedContent(metadata.Content, apiUrl, out var updatedContent))
            {
                return null;
            }

            updatedContent = VercelJsonRewrites.EnrichWithBuildSettings(updatedContent, websiteConfig);

            var commitSha = await _gitHubService.UpsertFileAsync(
                gitHubToken,
                repoParts[0],
                repoParts[1],
                candidatePath,
                updatedContent,
                "DeployAI: add API proxy rewrites for Railway backend",
                branch,
                metadata.Sha,
                cancellationToken);

            return commitSha;
        }

        var createPath = VercelJsonRewrites.ResolvePrimaryPath(websiteConfig);

        if (!VercelJsonRewrites.TryBuildUpdatedContent(null, apiUrl, out var createdContent))
        {
            return null;
        }

        createdContent = VercelJsonRewrites.EnrichWithBuildSettings(createdContent, websiteConfig);

        var createCommitSha = await _gitHubService.UpsertFileAsync(
            gitHubToken,
            repoParts[0],
            repoParts[1],
            createPath,
            createdContent,
            "DeployAI: create vercel.json with API proxy rewrites for Railway backend",
            branch,
            existingSha: null,
            cancellationToken);

        return createCommitSha;
    }

    private async Task<bool?> ProbeProxiedApiPostAsync(
        string websiteUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(FrontendEnvironmentWiringService));
            client.Timeout = TimeSpan.FromSeconds(20);
            var result = await DeploymentEndpointProbes.CheckProxiedApiLoginAsync(client, websiteUrl, cancellationToken);
            return result.Status switch
            {
                ProbeCheckStatus.Passed => true,
                ProbeCheckStatus.Failed => false,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool?> ProbeDeployedSpaWiredToApiAsync(
        string websiteUrl,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        _ = apiUrl;
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(FrontendEnvironmentWiringService));
            client.Timeout = TimeSpan.FromSeconds(20);
            return await DeploymentEndpointProbes.ProbeDeployedSpaWiredToApiAsync(
                client,
                websiteUrl,
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool?> ProbeRailwayAuthEndpointAsync(
        string apiUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(FrontendEnvironmentWiringService));
            client.Timeout = TimeSpan.FromSeconds(20);
            return await EvaluateRailwayAuthPostResponseAsync(
                client,
                CrossProviderUrlWiring.NormalizeOrigin(apiUrl),
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool?> EvaluateRailwayAuthPostResponseAsync(
        HttpClient client,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{apiUrl.TrimEnd('/')}/api/v1/auth/login");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Scans the project's repository for the split-origin wiring files (env script,
    /// api-base interceptor, ...). Returns the Blocking findings, or null when the
    /// repository could not be scanned.
    /// </summary>
    private async Task<IReadOnlyList<MissingDeploymentFile>?> TryGetSplitOriginBlockingIssuesAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var readiness = await _deploymentReadiness.ScanProjectAsync(projectId, gitRef: null, cancellationToken);
            return readiness.MissingFiles
                .Where(file => file.Severity == DeploymentFileSeverity.Blocking)
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> DetectStaleSplitOriginBuildDriftAsync(
        Guid projectId,
        string websiteUrl,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        var spaWired = await ProbeDeployedSpaWiredToApiAsync(websiteUrl, apiUrl, cancellationToken);
        if (spaWired != false)
        {
            return null;
        }

        // Only report drift when a rebuild can actually fix it; an app whose repo lacks
        // the split-origin wiring files would otherwise trigger a redeploy on every run.
        var blockingIssues = await TryGetSplitOriginBlockingIssuesAsync(projectId, cancellationToken);
        if (blockingIssues is null || blockingIssues.Count > 0)
        {
            return null;
        }

        return $"Vercel deployment missing split-origin bundle wiring (apiBaseInterceptor and apiBaseUrl required; production redeploy required).";
    }

    private sealed record ServerWiringContext(
        Project Project,
        DeployTarget? WebsiteDeployTarget,
        DeployTarget ServerDeployTarget,
        string? ApiUrl,
        string? ResolvedWebsiteUrl);
}
