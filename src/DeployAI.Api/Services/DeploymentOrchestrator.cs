using DeployAI.Api.Hubs;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Hangfire;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeployAI.Api.Services;

/// <summary>
/// Creates deployment records and queues a background job (<see cref="DeploymentJobRunner"/>,
/// below) per target to actually run each deploy. This file also defines
/// <see cref="DeploymentJobRunner"/>, which does the real per-target work: pushing Coolify config,
/// provisioning databases, wiring cross-provider env vars, and calling the provider to deploy.
/// </summary>
public sealed class DeploymentOrchestrator : IDeploymentOrchestrator
{
    private readonly DeployAIDbContext _db;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IDeploymentReadinessService _readinessService;
    private readonly IGitHubService _gitHubService;
    private readonly IEncryptionService _encryption;

    public DeploymentOrchestrator(
        DeployAIDbContext db,
        IBackgroundJobClient backgroundJobs,
        IDeploymentReadinessService readinessService,
        IGitHubService gitHubService,
        IEncryptionService encryption)
    {
        _db = db;
        _backgroundJobs = backgroundJobs;
        _readinessService = readinessService;
        _gitHubService = gitHubService;
        _encryption = encryption;
    }

    public async Task<TriggerDeploymentResult> TriggerAsync(
        Guid projectId,
        Guid userId,
        string branch,
        CancellationToken cancellationToken,
        DeploymentTriggeredBy triggeredBy = DeploymentTriggeredBy.User)
    {
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);

        if (project is null)
        {
            throw new DeployAIException("project_not_found", "We couldn't find that app.");
        }

        if (project.DeployTargets.Count == 0)
        {
            throw new DeployAIException("no_targets", "Connect a hosting destination before publishing.");
        }

        var readiness = await _readinessService.ScanProjectAsync(projectId, branch, cancellationToken);
        if (!readiness.IsReady)
        {
            throw new DeployAIException(
                "deployment_not_ready",
                "Deployment files are missing or invalid. Generate the split-origin setup before publishing.");
        }

        var gitHubToken = _encryption.Decrypt(project.User.GitHubTokenEncrypted);
        var repoParts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var commitSha = readiness.CommitSha;
        string? commitMessage = null;
        if (repoParts.Length == 2 && !string.IsNullOrWhiteSpace(commitSha))
        {
            var commit = await _gitHubService.GetCommitAsync(gitHubToken, repoParts[0], repoParts[1], commitSha, cancellationToken);
            commitMessage = commit?.Commit.Message;
        }

        var deployment = new Deployment
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Branch = branch,
            TriggeredBy = DeploymentTriggeredByValues.ToApiValue(triggeredBy),
            Status = DeploymentStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            GitCommitSha = commitSha,
            GitCommitMessage = commitMessage
        };

        var targetResults = new List<TriggerDeploymentTargetResult>();
        var enqueuedTargetIds = new List<Guid>();
        foreach (var target in project.DeployTargets.Where(t => DeployTargetConfig.Parse(t.ConfigJson).IsDeployableTarget))
        {
            var deploymentTarget = new DeploymentTarget
            {
                Id = Guid.NewGuid(),
                DeploymentId = deployment.Id,
                DeployTargetId = target.Id,
                ProviderName = target.ProviderName,
                Status = DeploymentStatuses.Pending
            };

            deployment.Targets.Add(deploymentTarget);
            targetResults.Add(new TriggerDeploymentTargetResult(target.ProviderName, DeploymentStatuses.Pending));
            enqueuedTargetIds.Add(deploymentTarget.Id);
        }

        if (targetResults.Count == 0)
        {
            throw new DeployAIException("no_targets", "Connect a hosting destination before publishing.");
        }

        _db.Deployments.Add(deployment);
        await _db.SaveChangesAsync(cancellationToken);

        var orderedTargetIds = OrderDeploymentTargetIds(deployment, project, enqueuedTargetIds);
        string? parentJobId = null;
        foreach (var deploymentTargetId in orderedTargetIds)
        {
            if (parentJobId is null)
            {
                parentJobId = _backgroundJobs.Enqueue<DeploymentJobRunner>(
                    runner => runner.RunAsync(deploymentTargetId, CancellationToken.None));
            }
            else
            {
                parentJobId = _backgroundJobs.ContinueJobWith<DeploymentJobRunner>(
                    parentJobId,
                    runner => runner.RunAsync(deploymentTargetId, CancellationToken.None));
            }
        }

        return new TriggerDeploymentResult(deployment.Id, deployment.Status, targetResults);
    }

    public async Task<TriggerDeploymentResult> TriggerTargetAsync(
        Guid projectId,
        Guid userId,
        Guid deployTargetId,
        string branch,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);

        if (project is null)
        {
            throw new DeployAIException("project_not_found", "We couldn't find that app.");
        }

        var deployTarget = project.DeployTargets.FirstOrDefault(t => t.Id == deployTargetId);
        if (deployTarget is null)
        {
            throw new DeployAIException("not_found", "We couldn't find that service.");
        }

        var targetConfig = DeployTargetConfig.Parse(deployTarget.ConfigJson);
        if (!targetConfig.IsDeployableTarget)
        {
            throw new DeployAIException("invalid_target", "That service cannot be redeployed from DeployAI.");
        }

        var readiness = await _readinessService.ScanProjectAsync(projectId, branch, cancellationToken);
        if (!readiness.IsReady)
        {
            throw new DeployAIException(
                "deployment_not_ready",
                "Deployment files are missing or invalid. Generate the split-origin setup before publishing.");
        }

        var gitHubToken = _encryption.Decrypt(project.User.GitHubTokenEncrypted);
        var repoParts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var commitSha = readiness.CommitSha;
        string? commitMessage = null;
        if (repoParts.Length == 2 && !string.IsNullOrWhiteSpace(commitSha))
        {
            var commit = await _gitHubService.GetCommitAsync(gitHubToken, repoParts[0], repoParts[1], commitSha, cancellationToken);
            commitMessage = commit?.Commit.Message;
        }

        var deployment = new Deployment
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Branch = branch,
            TriggeredBy = "user",
            Status = DeploymentStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            GitCommitSha = commitSha,
            GitCommitMessage = commitMessage
        };

        var deploymentTarget = new DeploymentTarget
        {
            Id = Guid.NewGuid(),
            DeploymentId = deployment.Id,
            DeployTargetId = deployTarget.Id,
            ProviderName = deployTarget.ProviderName,
            Status = DeploymentStatuses.Pending
        };

        deployment.Targets.Add(deploymentTarget);
        _db.Deployments.Add(deployment);
        await _db.SaveChangesAsync(cancellationToken);

        _backgroundJobs.Enqueue<DeploymentJobRunner>(
            runner => runner.RunAsync(deploymentTarget.Id, CancellationToken.None));

        return new TriggerDeploymentResult(
            deployment.Id,
            deployment.Status,
            [new TriggerDeploymentTargetResult(deployTarget.ProviderName, DeploymentStatuses.Pending)]);
    }

    internal static IReadOnlyList<Guid> OrderDeploymentTargetIds(
        Deployment deployment,
        Project project,
        IReadOnlyList<Guid> deploymentTargetIds)
    {
        var targetById = deployment.Targets.ToDictionary(t => t.Id);
        var deployTargetsById = project.DeployTargets.ToDictionary(t => t.Id);

        return deploymentTargetIds
            .Select(id =>
            {
                var deploymentTarget = targetById[id];
                var deployTarget = deployTargetsById[deploymentTarget.DeployTargetId];
                var config = DeployTargetConfig.Parse(deployTarget.ConfigJson);
                return new
                {
                    Id = id,
                    Order = GetDeployOrder(config, deployTarget.ProviderName)
                };
            })
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .ToList();
    }

    private static int GetDeployOrder(DeployTargetConfig config, string providerName)
    {
        if (string.Equals(config.Role, "server", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(config.Role, "website", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (string.Equals(providerName, "railway", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(providerName, "vercel", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(config.Role, "website", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (string.Equals(config.Role, "server", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return 1;
        }

        return 1;
    }
}

/// <summary>
/// Runs one deploy target's worth of work as a Hangfire background job: pushes Coolify config
/// (if applicable), provisions databases, wires cross-provider env vars, triggers the provider
/// deploy, streams logs, and updates the target/deployment status when it completes.
/// </summary>
public sealed class DeploymentJobRunner
{
    private readonly DeployAIDbContext _db;
    private readonly IProviderFactory _providerFactory;
    private readonly IEncryptionService _encryption;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IGitHubService _gitHubService;
    private readonly IRailwayDatabaseProvisioningService _railwayDatabaseProvisioning;
    private readonly IFrontendEnvironmentWiringService _frontendEnvironmentWiring;
    private readonly ISsrWebsiteBuildProvisioner _ssrWebsiteBuildProvisioner;
    private readonly IObjectStorageAutoProvisioner _objectStorageAutoProvisioner;
    private readonly IRequiredConfigurationCheck _requiredConfiguration;
    private readonly IProviderManagementFactory _providerManagementFactory;
    private readonly IServerDockerfileProvisioner _serverDockerfileProvisioner;
    private readonly IProviderApplicationConfigSyncFactory _applicationConfigSyncFactory;
    private readonly IDeploymentFailureAnalyzer _failureAnalyzer;
    private readonly IHubContext<DeploymentHub> _hub;
    private readonly DeployAI.Infrastructure.Options.AnthropicOptions _anthropicOptions;
    private readonly IDeploymentNotificationService _deploymentNotifications;
    private readonly ILogger<DeploymentJobRunner> _logger;

    public DeploymentJobRunner(
        DeployAIDbContext db,
        IProviderFactory providerFactory,
        IEncryptionService encryption,
        IProviderCredentialTokenService tokens,
        IGitHubService gitHubService,
        IRailwayDatabaseProvisioningService railwayDatabaseProvisioning,
        IFrontendEnvironmentWiringService frontendEnvironmentWiring,
        ISsrWebsiteBuildProvisioner ssrWebsiteBuildProvisioner,
        IObjectStorageAutoProvisioner objectStorageAutoProvisioner,
        IRequiredConfigurationCheck requiredConfiguration,
        IProviderManagementFactory providerManagementFactory,
        IServerDockerfileProvisioner serverDockerfileProvisioner,
        IProviderApplicationConfigSyncFactory applicationConfigSyncFactory,
        IDeploymentFailureAnalyzer failureAnalyzer,
        IHubContext<DeploymentHub> hub,
        Microsoft.Extensions.Options.IOptions<DeployAI.Infrastructure.Options.AnthropicOptions> anthropicOptions,
        IDeploymentNotificationService deploymentNotifications,
        ILogger<DeploymentJobRunner> logger)
    {
        _db = db;
        _providerFactory = providerFactory;
        _encryption = encryption;
        _tokens = tokens;
        _gitHubService = gitHubService;
        _railwayDatabaseProvisioning = railwayDatabaseProvisioning;
        _frontendEnvironmentWiring = frontendEnvironmentWiring;
        _ssrWebsiteBuildProvisioner = ssrWebsiteBuildProvisioner;
        _objectStorageAutoProvisioner = objectStorageAutoProvisioner;
        _requiredConfiguration = requiredConfiguration;
        _providerManagementFactory = providerManagementFactory;
        _serverDockerfileProvisioner = serverDockerfileProvisioner;
        _applicationConfigSyncFactory = applicationConfigSyncFactory;
        _failureAnalyzer = failureAnalyzer;
        _hub = hub;
        _anthropicOptions = anthropicOptions.Value;
        _deploymentNotifications = deploymentNotifications;
        _logger = logger;
    }

    /// <summary>Entry point invoked by Hangfire for a single deployment target; a no-op if the target was already cancelled.</summary>
    public async Task RunAsync(Guid deploymentTargetId, CancellationToken cancellationToken)
    {
        var target = await _db.DeploymentTargets
            .Include(t => t.Deployment)
            .Include(t => t.DeployTarget)
            .ThenInclude(dt => dt.Credential)
            .FirstOrDefaultAsync(t => t.Id == deploymentTargetId, cancellationToken);

        if (target is null)
        {
            return;
        }

        if (target.Status == DeploymentStatuses.Cancelled)
        {
            return;
        }

        var deployment = target.Deployment;
        var deployTarget = target.DeployTarget;
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .ThenInclude(t => t.Credential)
            .FirstOrDefaultAsync(p => p.Id == deployment.ProjectId, cancellationToken);

        if (project is null)
        {
            return;
        }

        var targetConfig = DeployTargetConfig.Parse(deployTarget.ConfigJson);
        if (targetConfig.IsDatabaseTarget)
        {
            target.Status = DeploymentStatuses.Success;
            target.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            await BroadcastStatusAsync(deployment.Id, target.ProviderName, target.Status);
            await FinalizeDeploymentAsync(deployment.Id, cancellationToken);
            return;
        }

        target.Status = DeploymentStatuses.InProgress;
        target.StartedAt = DateTimeOffset.UtcNow;
        deployment.Status = DeploymentStatuses.InProgress;
        deployment.StartedAt ??= DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await BroadcastStatusAsync(deployment.Id, target.ProviderName, target.Status);

        try
        {
            if (string.Equals(target.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase))
            {
                var wiringCommitSha = await _frontendEnvironmentWiring.WireWebsiteTargetBeforeDeployAsync(
                    deployment.Id,
                    target,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(wiringCommitSha))
                {
                    deployment.GitCommitSha = wiringCommitSha;
                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        UPDATE deployments
                        SET "GitCommitSha" = {deployment.GitCommitSha}
                        WHERE "Id" = {deployment.Id}
                        """,
                        cancellationToken);
                }
            }

            var coolifyConfig = DeployTargetConfig.Parse(deployTarget.ConfigJson);
            if (string.Equals(target.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(coolifyConfig.Role, "website", StringComparison.OrdinalIgnoreCase))
            {
                await _frontendEnvironmentWiring.WireWebsiteTargetBeforeDeployAsync(
                    deployment.Id,
                    target,
                    cancellationToken);

                // Advisory: a framework that inlines env at build time can only see it through a
                // Dockerfile we own, because Nixpacks builds get none of the app's environment. If
                // this can't be arranged, deploy on the existing build pack rather than failing.
                try
                {
                    await _ssrWebsiteBuildProvisioner.EnsureAsync(
                        project,
                        deployTarget,
                        deployment.Branch,
                        cancellationToken);
                    DetachDeployTargetChanges();
                    targetConfig = DeployTargetConfig.Parse(deployTarget.ConfigJson);
                }
                catch (Exception buildEx) when (buildEx is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        buildEx,
                        "Could not prepare a Dockerfile build for website target {TargetId}; continuing.",
                        target.Id);
                }
            }
            else if (string.Equals(target.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
                     DeploymentPartRoles.Is(coolifyConfig.Role, DeploymentPartRoles.Server))
            {
                // The server's Dockerfile used to be generated once, when the application was
                // created, and never again -- so every later improvement to it reached new apps
                // only, and an existing app kept building from whatever was generated on the day it
                // was made. Regenerating here means a fix lands on the next deploy, the way the
                // website path already worked.
                //
                // Safe to call for any server: the provisioner looks for a csproj and returns null
                // when there is none, so a server that is not .NET is left alone.
                try
                {
                    await EnsureServerDockerfileAsync(project, deployTarget, deployment.Branch, cancellationToken);
                    DetachDeployTargetChanges();
                    targetConfig = DeployTargetConfig.Parse(deployTarget.ConfigJson);
                }
                catch (Exception buildEx) when (buildEx is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        buildEx,
                        "Could not refresh the generated Dockerfile for server target {TargetId}; continuing.",
                        target.Id);
                }

                // An app that stores files needs a bucket before it meets its first upload. Left to
                // the user to notice and ask for, it does not happen: the app falls back to the
                // container filesystem, accepts the upload, reports success, and loses the file on
                // the next deploy. Reads the repository and provisions when the evidence says so.
                //
                // Advisory, like the Dockerfile step above: a bucket that cannot be created must not
                // fail a deploy that would otherwise succeed. It is always reported, though — every
                // outcome that matters ends up in the deploy log the user reads.
                // What the code being deployed needs, against what this app actually has. Three
                // incidents in one day were this gap: a crash-loop on missing Jwt settings, every
                // route down on missing Storage settings, and Events 500ing on a missing Tickets
                // key. The build was green and the migration chain valid in all three -- the fault
                // was never in the code, it was between the code and the target.
                try
                {
                    var configuration = await _requiredConfiguration.CheckAsync(
                        project, deployTarget, deployment.Branch, cancellationToken);

                    if (!string.IsNullOrWhiteSpace(configuration.Message))
                    {
                        await PersistAndBroadcastLogAsync(
                            target,
                            deployment.Id,
                            await NextSequenceAsync(target.Id, cancellationToken),
                            configuration.Message,
                            cancellationToken);
                    }
                }
                catch (Exception configEx) when (configEx is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        configEx,
                        "Configuration check failed for server target {TargetId}; continuing.",
                        target.Id);
                }

                try
                {
                    var storage = await _objectStorageAutoProvisioner.EnsureAsync(
                        project, deployTarget, deployment.Branch, cancellationToken);

                    if (!string.IsNullOrWhiteSpace(storage.Message))
                    {
                        await PersistAndBroadcastLogAsync(
                            target,
                            deployment.Id,
                            await NextSequenceAsync(target.Id, cancellationToken),
                            storage.Message,
                            cancellationToken);
                    }
                }
                catch (Exception storageEx) when (storageEx is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        storageEx,
                        "Object storage check failed for server target {TargetId}; continuing.",
                        target.Id);
                }
            }

            var provider = _providerFactory.GetProvider(target.ProviderName);
            var token = await _tokens.GetTokenAsync(deployTarget.Credential, cancellationToken);
            var credentials = new ProviderCredentials(token);

            // Duplicate env-var records: repair on every deploy, for every target. This existed and
            // was tested, but ran from one place -- the database-linking path -- so an app that
            // never linked a database was never repaired, and a website never is. One project's
            // frontend was carrying three duplicated keys for exactly that reason.
            //
            // Placed here rather than in the role-specific branches above because it is the target
            // that can hold duplicates, not the role.
            try
            {
                var management = _providerManagementFactory.GetManagement(target.ProviderName);
                if (management is not null && !string.IsNullOrWhiteSpace(deployTarget.ProviderProjectId))
                {
                    var removed = await management.ReconcileDuplicateEnvVarsAsync(
                        credentials, deployTarget.ProviderProjectId!, cancellationToken);

                    if (removed > 0)
                    {
                        await PersistAndBroadcastLogAsync(
                            target,
                            deployment.Id,
                            await NextSequenceAsync(target.Id, cancellationToken),
                            $"Removed {removed} duplicate environment variable record(s) on this app. "
                            + "Duplicates make the value an app reads differ from the one shown.",
                            cancellationToken);
                    }
                }
            }
            catch (Exception reconcileEx) when (reconcileEx is not OperationCanceledException)
            {
                // Advisory: a repair that fails must not stop a deploy that would otherwise work.
                _logger.LogWarning(
                    reconcileEx,
                    "Could not reconcile duplicate env vars for target {TargetId}; continuing.",
                    target.Id);
            }

            if (string.Equals(target.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
            {
                var databaseNote = await _railwayDatabaseProvisioning.EnsureFromRepoAsync(
                    project,
                    deployTarget,
                    deployment.Branch,
                    cancellationToken);
                await ReportDatabaseScanAsync(target, deployment, databaseNote, cancellationToken);
                DetachDeployTargetChanges();
                targetConfig = DeployTargetConfig.Parse(deployTarget.ConfigJson);

                await _frontendEnvironmentWiring.WireServerTargetBeforeRailwayDeployAsync(
                    deployment.Id,
                    target,
                    cancellationToken);
            }

            if (string.Equals(target.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(targetConfig.Role, "server", StringComparison.OrdinalIgnoreCase))
            {
                var databaseNote = await _railwayDatabaseProvisioning.EnsureFromRepoAsync(
                    project,
                    deployTarget,
                    deployment.Branch,
                    cancellationToken);
                await ReportDatabaseScanAsync(target, deployment, databaseNote, cancellationToken);
                DetachDeployTargetChanges();
                targetConfig = DeployTargetConfig.Parse(deployTarget.ConfigJson);

                // Advisory: wiring the website's API URL onto the server is a convenience, not a
                // prerequisite for the build. A hiccup here (e.g. drift comparison against an app
                // whose env list has a duplicated key) must not fail the deploy before it starts.
                try
                {
                    await _frontendEnvironmentWiring.WireServerTargetBeforeRailwayDeployAsync(
                        deployment.Id,
                        target,
                        cancellationToken);
                }
                catch (Exception wiringEx) when (wiringEx is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        wiringEx,
                        "Pre-deploy environment wiring failed for server target {TargetId}; continuing with the build.",
                        target.Id);
                }
            }

            var environment = new Dictionary<string, string>
            {
                ["githubRepoFullName"] = project.GitHubRepoFullName
            };
            foreach (var entry in targetConfig.ToEnvironmentEntries())
            {
                environment[entry.Key] = entry.Value;
            }

            if (string.Equals(target.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(targetConfig.Framework, "docker", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyResolvedDockerBuildEnvironmentAsync(
                    project,
                    targetConfig,
                    deployment.Branch,
                    environment,
                    cancellationToken);
            }

            if (string.Equals(target.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
            {
                var commitSha = deployment.GitCommitSha ??
                    await ResolveGitHubCommitShaAsync(project, deployment.Branch, cancellationToken);
                if (!string.IsNullOrWhiteSpace(commitSha))
                {
                    environment["commitSha"] = commitSha;
                }
            }

            if (string.Equals(target.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(deployment.GitCommitSha))
            {
                environment["commitSha"] = deployment.GitCommitSha;
            }

            if (string.Equals(target.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
                _applicationConfigSyncFactory.GetConfigSync(target.ProviderName) is { } configSync)
            {
                // Coolify only picks up build pack/port/command changes made in DeployAI's UI
                // (ApplyTargetUpdates) if they're pushed to its application config explicitly - and
                // it caches generated Traefik labels across redeploys unless they're cleared too, so
                // push current config before every deploy rather than relying on whatever was set at
                // creation time.
                await configSync.UpdateApplicationConfigAsync(
                    credentials,
                    deployTarget.ProviderProjectId,
                    new UpdateProviderApplicationConfigRequest(
                        targetConfig.Framework,
                        targetConfig.RootDirectory,
                        targetConfig.OutputDirectory,
                        targetConfig.BuildCommand,
                        targetConfig.InstallCommand,
                        targetConfig.StartCommand,
                        targetConfig.DockerfilePath),
                    cancellationToken);
            }

            var response = await provider.TriggerDeploymentAsync(
                credentials,
                deployTarget.ProviderProjectId,
                deployment.Branch,
                environment,
                cancellationToken);

            target.ProviderDeploymentId = response.DeploymentId;
            target.DeployUrl = response.DeployUrl;
            DetachDeployTargetChanges();
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE deployment_targets
                SET "ProviderDeploymentId" = {target.ProviderDeploymentId}, "DeployUrl" = {target.DeployUrl}
                WHERE "Id" = {target.Id}
                """,
                cancellationToken);

            var sequence = 0;
            await foreach (var line in provider.StreamLogsAsync(credentials, response.DeploymentId, cancellationToken))
            {
                sequence++;
                await PersistAndBroadcastLogAsync(target, deployment.Id, sequence, line, cancellationToken);
            }

            var status = await provider.GetStatusAsync(credentials, response.DeploymentId, cancellationToken);
            target.Status = MapStatus(status.Status);
            target.DeployUrl ??= status.DeployUrl;
            target.CompletedAt = DateTimeOffset.UtcNow;

            if (target.Status == DeploymentStatuses.Failed && !string.IsNullOrWhiteSpace(status.ErrorMessage))
            {
                // #region agent log
                try
                {
                    var payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sessionId = "b63a0f",
                        hypothesisId = "D",
                        location = "DeploymentOrchestrator.cs:appendErrorMessage",
                        message = "inject_generic_error_log_line",
                        data = new
                        {
                            targetId = target.Id,
                            provider = target.ProviderName,
                            providerDeploymentId = target.ProviderDeploymentId,
                            sequenceBefore = sequence,
                            errorMessage = status.ErrorMessage
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    System.IO.File.AppendAllText(@"c:\Users\Abdulrahman\Desktop\DeployAI\debug-b63a0f.log", payload + Environment.NewLine);
                }
                catch { /* debug ingest */ }
                // #endregion
                sequence++;
                await PersistAndBroadcastLogAsync(target, deployment.Id, sequence, status.ErrorMessage!, cancellationToken);
            }

            if ((string.Equals(target.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase) ||
                 (string.Equals(target.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(DeployTargetConfig.Parse(deployTarget.ConfigJson).Role, "website", StringComparison.OrdinalIgnoreCase))) &&
                target.Status == DeploymentStatuses.Success)
            {
                if (string.Equals(target.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase) &&
                    provider is IWebsiteApiProxySupport websiteProxy)
                {
                    try
                    {
                        var stableUrl = await websiteProxy.ResolvePublicWebsiteUrlAsync(
                            credentials,
                            deployTarget.ProviderProjectId,
                            target.DeployUrl,
                            cancellationToken);
                        if (!string.IsNullOrWhiteSpace(stableUrl))
                        {
                            target.DeployUrl = stableUrl;
                        }
                    }
                    catch
                    {
                        // Resolving the stable domain is best-effort; keep the deployment URL on failure.
                    }
                }

                // Post-deploy wiring and verification are advisory: the deploy itself already
                // succeeded, and an exception here must not flip it to failed — that is
                // exactly how a live compose deploy got reported failed after its rollout
                // completed. Log the problem and keep the success.
                try
                {
                    await _frontendEnvironmentWiring.WireServerTargetAfterWebsiteDeployAsync(
                        deployment.Id,
                        target,
                        cancellationToken);

                    var verificationMessages = await _frontendEnvironmentWiring.VerifyWiredEndpointsAsync(
                        deployment.Id,
                        cancellationToken);
                    foreach (var message in verificationMessages)
                    {
                        sequence++;
                        await PersistAndBroadcastLogAsync(target, deployment.Id, sequence, message, cancellationToken);
                    }
                }
                catch (Exception wiringEx) when (wiringEx is not OperationCanceledException)
                {
                    sequence++;
                    await PersistAndBroadcastLogAsync(
                        target,
                        deployment.Id,
                        sequence,
                        $"Post-deploy check could not complete: {wiringEx.Message}",
                        cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            target.Status = DeploymentStatuses.Failed;
            target.CompletedAt = DateTimeOffset.UtcNow;

            // Previously the exception was swallowed and only "Something went wrong while
            // publishing" was recorded, which made a pre-deploy failure (e.g. database
            // provisioning throwing before the build even starts) impossible to diagnose.
            // Log the full exception server-side and persist a message that names the cause.
            _logger.LogError(
                ex,
                "Deploy target {TargetId} ({Provider}, {Role}) failed before or during publish for deployment {DeploymentId}.",
                target.Id,
                target.ProviderName,
                DeployTargetConfig.Parse(deployTarget.ConfigJson).Role,
                deployment.Id);

            var userMessage = ex switch
            {
                DeployAIException deployAiException => deployAiException.Message,
                _ => $"Publishing failed before the build started: {ex.Message}"
            };
            await PersistAndBroadcastLogAsync(
                target,
                deployment.Id,
                await NextSequenceAsync(target.Id, cancellationToken),
                userMessage,
                cancellationToken);
        }

        DetachDeployTargetChanges();
        await PersistDeploymentTargetStateAsync(target, cancellationToken);
        await BroadcastStatusAsync(deployment.Id, target.ProviderName, target.Status);
        await FinalizeDeploymentAsync(deployment.Id, cancellationToken);
    }

    /// <summary>
    /// Rewrites the generated Dockerfile for a .NET server so the current generator's output is
    /// what actually gets built. Returns quietly for a server with no csproj.
    /// </summary>
    private async Task EnsureServerDockerfileAsync(
        Data.Entities.Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken)
    {
        var parts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return;
        }

        var config = DeployTargetConfig.Parse(serverTarget.ConfigJson);
        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var githubToken = _encryption.Decrypt(user.GitHubTokenEncrypted);

        await _serverDockerfileProvisioner.EnsureDockerfileAsync(
            githubToken,
            parts[0],
            parts[1],
            branch,
            config.RootDirectory ?? string.Empty,
            config.ServiceDirectory,
            cancellationToken);
    }

    private void DetachDeployTargetChanges()
    {
        foreach (var entry in _db.ChangeTracker.Entries<DeployTarget>())
        {
            entry.State = EntityState.Unchanged;
        }
    }

    private async Task PersistDeploymentTargetStateAsync(
        DeploymentTarget target,
        CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE deployment_targets
            SET "Status" = {target.Status},
                "StartedAt" = {target.StartedAt},
                "CompletedAt" = {target.CompletedAt},
                "ProviderDeploymentId" = {target.ProviderDeploymentId},
                "DeployUrl" = {target.DeployUrl}
            WHERE "Id" = {target.Id}
            """,
            cancellationToken);
    }

    private async Task FinalizeDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        var deployment = await _db.Deployments
            .Include(d => d.Targets)
            .FirstAsync(d => d.Id == deploymentId, cancellationToken);

        if (deployment.Targets.Any(t => t.Status is DeploymentStatuses.Pending or DeploymentStatuses.InProgress))
        {
            return;
        }

        if (deployment.Targets.All(t => t.Status == DeploymentStatuses.Cancelled))
        {
            deployment.Status = DeploymentStatuses.Cancelled;
            deployment.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var successCount = deployment.Targets.Count(t => t.Status == DeploymentStatuses.Success);
        deployment.Status = successCount switch
        {
            0 => DeploymentStatuses.Failed,
            var count when count == deployment.Targets.Count => DeploymentStatuses.Success,
            _ => DeploymentStatuses.Partial
        };
        deployment.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var failedTarget in deployment.Targets.Where(t => t.Status == DeploymentStatuses.Failed))
        {
            var logLines = await _db.DeploymentLogs
                .Where(l => l.DeploymentTargetId == failedTarget.Id)
                .OrderBy(l => l.Sequence)
                .Select(l => l.Line)
                .ToListAsync(cancellationToken);

            var analysis = _failureAnalyzer.Analyze(failedTarget.ProviderName, logLines);
            // #region agent log
            try
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    sessionId = "b63a0f",
                    runId = "post-fix",
                    hypothesisId = "E",
                    location = "DeploymentOrchestrator.cs:failureAnalysis",
                    message = "failure_analysis_result",
                    data = new
                    {
                        targetId = failedTarget.Id,
                        provider = failedTarget.ProviderName,
                        logLineCount = logLines.Count,
                        logPreview = logLines.Take(5).ToArray(),
                        category = analysis.Category.ToString(),
                        summary = analysis.Summary,
                        canRequestClaudeFix = analysis.CanRequestClaudeFix,
                        referencedFileCount = analysis.ReferencedFiles.Count,
                        hasExcerpt = !string.IsNullOrWhiteSpace(analysis.ErrorExcerpt)
                    },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                System.IO.File.AppendAllText(@"c:\Users\Abdulrahman\Desktop\DeployAI\debug-b63a0f.log", payload + Environment.NewLine);
            }
            catch { /* debug ingest */ }
            // #endregion
            failedTarget.FailureAnalysisJson = DeploymentFailureAnalysisJson.ToJson(analysis);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE deployment_targets
                SET "FailureAnalysisJson" = {failedTarget.FailureAnalysisJson}
                WHERE "Id" = {failedTarget.Id}
                """,
                cancellationToken);

            if (_anthropicOptions.MemoryEnabled)
            {
                var memory = new AgentMemoryToolHandler(_db, deployment.ProjectId);
                await memory.AppendHistoryAsync(
                    $"Failure: {failedTarget.ProviderName} — {analysis.Category} — {analysis.Summary}",
                    cancellationToken);
            }

            await _hub.Clients.Group(deploymentId.ToString())
                .SendAsync(
                    "FailureAnalysisReady",
                    deploymentId,
                    failedTarget.Id,
                    failedTarget.ProviderName,
                    new
                    {
                        category = analysis.Category switch
                        {
                            DeploymentFailureCategory.CodeBuild => "code_build",
                            DeploymentFailureCategory.Infrastructure => "infrastructure",
                            _ => "unknown"
                        },
                        summary = analysis.Summary,
                        errorExcerpt = analysis.ErrorExcerpt,
                        referencedFiles = analysis.ReferencedFiles,
                        canRequestClaudeFix = analysis.CanRequestClaudeFix
                    },
                    cancellationToken);
        }

        var hasLiveRailway = deployment.Targets.Any(t =>
            string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
            t.Status == DeploymentStatuses.Success &&
            !string.IsNullOrWhiteSpace(t.DeployUrl));
        var hasLiveVercel = deployment.Targets.Any(t =>
            string.Equals(t.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase) &&
            t.Status == DeploymentStatuses.Success &&
            !string.IsNullOrWhiteSpace(t.DeployUrl));

        if (hasLiveRailway && hasLiveVercel)
        {
            var syncResult = await _frontendEnvironmentWiring.SyncCrossProviderEnvironmentAsync(
                deployment.ProjectId,
                new EnvironmentSyncOptions(
                    RedeployRailwayAfterUpdate: true,
                    EnsureWebsiteWiring: true,
                    ApplyVercelEnv: true,
                    ApplyRailwayEnv: true,
                    RunVerification: true,
                    Source: "deploy"),
                cancellationToken);

            var logTarget = deployment.Targets.FirstOrDefault(t =>
                string.Equals(t.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase))
                ?? deployment.Targets.First();

            var sequence = await NextSequenceAsync(logTarget.Id, cancellationToken);
            await PersistAndBroadcastLogAsync(
                logTarget,
                deployment.Id,
                sequence,
                "Cross-provider environment sync completed.",
                cancellationToken);

            foreach (var message in syncResult.VerificationMessages)
            {
                sequence++;
                await PersistAndBroadcastLogAsync(logTarget, deployment.Id, sequence, message, cancellationToken);
            }
        }

        await _hub.Clients.Group(deploymentId.ToString())
            .SendAsync("DeploymentCompleted", deploymentId, deployment.Status, cancellationToken);

        await _deploymentNotifications.NotifyDeploymentCompletedAsync(deploymentId, cancellationToken);
    }

    /// <summary>
    /// Says so in the deploy log when the database scan could not read the repository.
    /// </summary>
    /// <remarks>
    /// Provisioning nothing because the app needs nothing, and provisioning nothing because nobody
    /// could look, used to produce identical output: none. Only one of them is safe.
    /// </remarks>
    private async Task ReportDatabaseScanAsync(
        DeploymentTarget target,
        Deployment deployment,
        string? note,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        await PersistAndBroadcastLogAsync(
            target,
            deployment.Id,
            await NextSequenceAsync(target.Id, cancellationToken),
            note,
            cancellationToken);
    }

    private async Task PersistAndBroadcastLogAsync(
        DeploymentTarget target,
        Guid deploymentId,
        int sequence,
        string line,
        CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO deployment_logs ("DeploymentTargetId", "Line", "LoggedAt", "Sequence")
            VALUES ({target.Id}, {line}, {DateTimeOffset.UtcNow}, {sequence})
            """,
            cancellationToken);

        await _hub.Clients.Group(deploymentId.ToString())
            .SendAsync("LogLine", deploymentId, target.ProviderName, sequence, line, cancellationToken);
    }

    private async Task BroadcastStatusAsync(Guid deploymentId, string providerName, string status, CancellationToken cancellationToken = default)
    {
        await _hub.Clients.Group(deploymentId.ToString())
            .SendAsync("StatusChanged", deploymentId, providerName, status, cancellationToken);
    }

    private async Task<int> NextSequenceAsync(Guid deploymentTargetId, CancellationToken cancellationToken)
    {
        var max = await _db.DeploymentLogs
            .Where(l => l.DeploymentTargetId == deploymentTargetId)
            .Select(l => (int?)l.Sequence)
            .MaxAsync(cancellationToken);
        return (max ?? 0) + 1;
    }

    private static string MapStatus(DeploymentStatusKind status) => status switch
    {
        DeploymentStatusKind.Success => DeploymentStatuses.Success,
        DeploymentStatusKind.Failed => DeploymentStatuses.Failed,
        DeploymentStatusKind.InProgress => DeploymentStatuses.InProgress,
        DeploymentStatusKind.Pending => DeploymentStatuses.Pending,
        _ => DeploymentStatuses.InProgress
    };

    private async Task<string?> ResolveGitHubCommitShaAsync(
        Project project,
        string branch,
        CancellationToken cancellationToken)
    {
        var parts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var token = _encryption.Decrypt(user.GitHubTokenEncrypted);
        return await _gitHubService.GetBranchHeadShaAsync(token, parts[0], parts[1], branch, cancellationToken);
    }

    private async Task ApplyResolvedDockerBuildEnvironmentAsync(
        Project project,
        DeployTargetConfig config,
        string branch,
        IDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var serviceDirectory = config.ServiceDirectory?.Trim().Trim('/')
            ?? config.RootDirectory?.Trim().Trim('/')
            ?? string.Empty;

        var dockerfilePathInRepo = config.DockerfilePath?.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(dockerfilePathInRepo))
        {
            dockerfilePathInRepo = string.IsNullOrEmpty(serviceDirectory)
                ? "Dockerfile"
                : $"{serviceDirectory}/Dockerfile";
        }

        var parts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return;
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var token = _encryption.Decrypt(user.GitHubTokenEncrypted);
        var dockerfileContent = await _gitHubService.GetFileContentAsync(
            token,
            parts[0],
            parts[1],
            dockerfilePathInRepo,
            branch,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(dockerfileContent))
        {
            return;
        }

        foreach (var entry in DockerBuildEnvironmentResolver.Resolve(
                     dockerfileContent,
                     serviceDirectory,
                     config.DockerfilePath))
        {
            environment[entry.Key] = entry.Value;
        }
    }
}
