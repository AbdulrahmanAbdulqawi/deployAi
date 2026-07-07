using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DeployAI.Api.Services;

public interface IFrontendEnvironmentWiringService
{
    Task<EnvironmentSyncResult> SyncCrossProviderEnvironmentAsync(
        Guid projectId,
        EnvironmentSyncOptions options,
        CancellationToken cancellationToken);

    Task<string?> WireWebsiteTargetBeforeDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken);

    Task WireServerTargetBeforeRailwayDeployAsync(
        Guid deploymentId,
        DeploymentTarget railwayTarget,
        CancellationToken cancellationToken);

    Task WireServerTargetAfterWebsiteDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken);

    Task SyncRailwayServerRuntimeEnvAsync(
        Guid projectId,
        Guid serverDeployTargetId,
        bool redeployAfterUpdate,
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
    private readonly IProviderFactory _providerFactory;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IGitHubService _gitHubService;
    private readonly IEncryptionService _encryption;
    private readonly IHttpClientFactory _httpClientFactory;

    public FrontendEnvironmentWiringService(
        DeployAIDbContext db,
        IProviderManagementFactory managementFactory,
        IProviderServiceOperationsFactory serviceOperationsFactory,
        IProviderFactory providerFactory,
        IProviderCredentialTokenService tokens,
        IGitHubService gitHubService,
        IEncryptionService encryption,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _managementFactory = managementFactory;
        _serviceOperationsFactory = serviceOperationsFactory;
        _providerFactory = providerFactory;
        _tokens = tokens;
        _gitHubService = gitHubService;
        _encryption = encryption;
        _httpClientFactory = httpClientFactory;
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

        var websiteDeployTarget = FindWebsiteDeployTarget(project);
        var serverDeployTarget = FindServerDeployTarget(project);
        if (websiteDeployTarget?.Credential is null || serverDeployTarget?.Credential is null)
        {
            return SkippedResult(options.Source, completedAt, "Project does not have both Railway and Vercel targets.");
        }

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
                "Could not resolve live Railway API URL and Vercel website URL.");
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

        if (options.DetectDriftOnly)
        {
            var driftOnlyResult = new EnvironmentSyncResult(
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
            await PersistSyncStateAsync(project, driftOnlyResult, cancellationToken);
            return driftOnlyResult;
        }

        var hadVercelDrift = driftDetails.Any(d => d.StartsWith("Vercel ", StringComparison.Ordinal));

        var railwayKeysApplied = new List<string>();
        var vercelKeysApplied = new List<string>();

        if (options.ApplyVercelEnv)
        {
            vercelKeysApplied.AddRange(await ApplyVercelApiEnvironmentAsync(
                websiteDeployTarget,
                websiteConfig.Framework,
                apiUrl,
                cancellationToken));
        }

        var vercelRedeployTriggered = false;
        string? vercelCommitSha = null;
        string? domainAssignmentMessage = null;
        if (options.EnsureWebsiteWiring)
        {
            vercelCommitSha = await EnsureWebsiteWiringAsync(
                project,
                websiteDeployTarget,
                websiteConfig,
                serverConfig.Framework,
                apiUrl,
                cancellationToken);
        }

        var usesSplitOrigin = CrossProviderUrlWiring.ShouldUseSplitOrigin(
            websiteConfig.Framework,
            serverConfig.Framework);
        var shouldRedeployVercel = options.RedeployVercelAfterUpdate ||
            !string.IsNullOrWhiteSpace(vercelCommitSha) ||
            hadVercelDrift;

        if (options.EnsureWebsiteWiring &&
            !usesSplitOrigin &&
            CrossProviderUrlWiring.UsesRelativeApiPaths(websiteConfig.Framework) &&
            !shouldRedeployVercel)
        {
            var proxyWorking = await ProbeProxiedApiPostAsync(websiteUrl, cancellationToken);
            if (proxyWorking == false)
            {
                shouldRedeployVercel = true;
            }
        }

        if (shouldRedeployVercel)
        {
            var triggeredDeploymentId = await TriggerVercelProductionRedeployAsync(
                project,
                websiteDeployTarget,
                websiteConfig,
                vercelCommitSha,
                cancellationToken);
            vercelRedeployTriggered = !string.IsNullOrWhiteSpace(triggeredDeploymentId);
        }
        else if (options.EnsureWebsiteWiring)
        {
            domainAssignmentMessage = await EnsureVercelProductionDomainsAsync(
                websiteDeployTarget,
                deploymentId: null,
                cancellationToken);
        }

        if (options.ApplyRailwayEnv)
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

        if (options.RedeployRailwayAfterUpdate)
        {
            var serviceOperations = _serviceOperationsFactory.GetServiceOperations("railway");
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
        }

        var result = new EnvironmentSyncResult(
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

        await PersistSyncStateAsync(project, result, cancellationToken);
        return result;
    }

    public async Task<string?> WireWebsiteTargetBeforeDeployAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(websiteTarget.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase))
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
        if (!string.Equals(railwayTarget.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
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
        if (!string.Equals(websiteTarget.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase) ||
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
                RunVerification: false,
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
        var serverDeployTarget = deployment.Project.DeployTargets
            .FirstOrDefault(t => t.Id == serverTarget.DeployTargetId);
        var websiteConfig = DeployTargetConfig.Parse(websiteDeployTarget?.ConfigJson);
        var serverConfig = DeployTargetConfig.Parse(serverDeployTarget?.ConfigJson);
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

        if (CrossProviderUrlWiring.ShouldUseSplitOrigin(websiteConfig?.Framework, serverConfig?.Framework))
        {
            await VerifySplitOriginApiHealthAsync(
                client,
                $"{apiUrl}/api/v1/health",
                label: "Railway API health",
                messages,
                cancellationToken);
        }
        else if (CrossProviderUrlWiring.UsesRelativeApiPaths(websiteConfig?.Framework))
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
                string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(t.DeployUrl));

        var serverDeployTarget = serverDeploymentTarget is null
            ? deployment.Project.DeployTargets.FirstOrDefault(t =>
                string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
                DeployTargetConfig.Parse(t.ConfigJson).IsDeployableTarget)
            : deployment.Project.DeployTargets.FirstOrDefault(t => t.Id == serverDeploymentTarget.DeployTargetId);

        var apiUrl = serverDeploymentTarget?.DeployUrl;
        if (string.IsNullOrWhiteSpace(apiUrl) &&
            serverDeployTarget?.Credential is not null &&
            _serviceOperationsFactory.GetServiceOperations("railway") is { } railwayOperations)
        {
            var serverCredentials = await GetCredentialsAsync(serverDeployTarget, cancellationToken);
            var status = await railwayOperations.GetServiceStatusAsync(
                serverCredentials,
                serverDeployTarget.ProviderProjectId,
                cancellationToken);
            apiUrl = status.DeployUrl;
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

    private static DeployTarget? FindWebsiteDeployTarget(Project project) =>
        project.DeployTargets.FirstOrDefault(t =>
            string.Equals(t.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(DeployTargetConfig.Parse(t.ConfigJson).Role, "website", StringComparison.OrdinalIgnoreCase))
        ?? project.DeployTargets.FirstOrDefault(t =>
            string.Equals(t.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase));

    private async Task<string?> ResolveLiveApiUrlAsync(
        DeployTarget serverDeployTarget,
        CancellationToken cancellationToken)
    {
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

    private static DeployTarget? FindServerDeployTarget(Project project) =>
        project.DeployTargets.FirstOrDefault(t =>
            string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
            DeployTargetConfig.Parse(t.ConfigJson).IsDeployableTarget);

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
        var expectedRailway = CrossProviderUrlWiring.BuildExpectedRailwayEnvValues(
            serverFramework,
            websiteFramework,
            websiteUrl,
            websiteOrigins,
            apiUrl);
        var expectedVercel = CrossProviderUrlWiring.BuildExpectedVercelEnvValues(websiteFramework, apiUrl);

        var railwayManagement = _managementFactory.GetManagement("railway");
        var vercelManagement = _managementFactory.GetManagement("vercel");
        var railwayCredentials = await GetCredentialsAsync(serverDeployTarget, cancellationToken);
        var vercelCredentials = await GetCredentialsAsync(websiteDeployTarget, cancellationToken);

        var railwayEnvVars = await railwayManagement.ListEnvVarsAsync(
            railwayCredentials,
            serverDeployTarget.ProviderProjectId,
            cancellationToken);
        var vercelEnvVars = await vercelManagement.ListEnvVarsAsync(
            vercelCredentials,
            websiteDeployTarget.ProviderProjectId,
            cancellationToken);

        CompareExpectedEnvVars(driftDetails, "Railway", expectedRailway, railwayEnvVars);
        CompareExpectedEnvVars(driftDetails, "Vercel", expectedVercel, vercelEnvVars, skipHiddenValues: true);

        if (CrossProviderUrlWiring.ShouldUseSplitOrigin(websiteFramework, serverFramework))
        {
            foreach (var warning in CrossProviderUrlWiring.ValidateIgnoredRailwayEnvKeys(
                         railwayEnvVars.Select(env => new CrossProviderUrlWiring.ProviderEnvVarSnapshot(env.Key, env.Value)).ToArray()))
            {
                driftDetails.Add(warning);
            }
        }

        return driftDetails;
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

        await VerifyReachableAsync(
            client,
            $"{apiUrl}/",
            label: "Railway API",
            messages,
            cancellationToken);

        if (CrossProviderUrlWiring.ShouldUseSplitOrigin(websiteFramework, serverFramework))
        {
            await VerifySplitOriginApiHealthAsync(
                client,
                $"{apiUrl}/api/v1/health",
                label: "Railway API health",
                messages,
                cancellationToken);
        }
        else if (CrossProviderUrlWiring.UsesRelativeApiPaths(websiteFramework))
        {
            await VerifyProxiedApiLoginAsync(
                client,
                websiteUrl,
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

    private async Task<bool?> ProbeProxiedApiPostAsync(
        string websiteUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(FrontendEnvironmentWiringService));
            client.Timeout = TimeSpan.FromSeconds(20);
            return await EvaluateProxiedApiPostResponseAsync(
                client,
                websiteUrl,
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool?> EvaluateProxiedApiPostResponseAsync(
        HttpClient client,
        string websiteUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{websiteUrl.TrimEnd('/')}/api/v1/auth/login");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";

        if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
        {
            return false;
        }

        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static async Task VerifyProxiedApiLoginAsync(
        HttpClient client,
        string websiteUrl,
        string label,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxyWorking = await EvaluateProxiedApiPostResponseAsync(
                client,
                websiteUrl,
                cancellationToken);

            if (proxyWorking == false)
            {
                messages.Add($"{label} check returned 405 for POST /api/v1/auth/login. Production domain may still point at an old Vercel deployment.");
                return;
            }

            if (proxyWorking is null)
            {
                messages.Add($"{label} check error: could not reach POST /api/v1/auth/login");
                return;
            }

            messages.Add($"{label} check passed: POST /api/v1/auth/login");
        }
        catch (Exception ex)
        {
            messages.Add($"{label} check error: {ex.Message}");
        }
    }

    private static async Task VerifySplitOriginApiHealthAsync(
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
            if (!response.IsSuccessStatusCode)
            {
                messages.Add($"{label} check failed ({(int)response.StatusCode}): {url}");
                return;
            }

            if (!body.Contains("healthy", StringComparison.OrdinalIgnoreCase))
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
        var preflightUrl = url.TrimEnd('/') + "/api/v1/auth/login";
        using var request = new HttpRequestMessage(HttpMethod.Options, preflightUrl);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                messages.Add($"{label} check failed ({(int)response.StatusCode}): {preflightUrl}");
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

    private sealed record ServerWiringContext(
        Project Project,
        DeployTarget? WebsiteDeployTarget,
        DeployTarget ServerDeployTarget,
        string? ApiUrl,
        string? ResolvedWebsiteUrl);
}
