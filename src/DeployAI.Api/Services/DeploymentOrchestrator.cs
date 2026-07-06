using DeployAI.Api.Hubs;
using DeployAI.Api.Services;
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

namespace DeployAI.Api.Services;

public sealed class DeploymentOrchestrator : IDeploymentOrchestrator
{
    private readonly DeployAIDbContext _db;
    private readonly IBackgroundJobClient _backgroundJobs;

    public DeploymentOrchestrator(DeployAIDbContext db, IBackgroundJobClient backgroundJobs)
    {
        _db = db;
        _backgroundJobs = backgroundJobs;
    }

    public async Task<TriggerDeploymentResult> TriggerAsync(Guid projectId, Guid userId, string branch, CancellationToken cancellationToken)
    {
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);

        if (project is null)
        {
            throw new DeployAIException("project_not_found", "We couldn't find that app.");
        }

        if (project.DeployTargets.Count == 0)
        {
            throw new DeployAIException("no_targets", "Connect a hosting destination before publishing.");
        }

        var deployment = new Deployment
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Branch = branch,
            TriggeredBy = "user",
            Status = DeploymentStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow
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

        return 1;
    }
}

public sealed class DeploymentJobRunner
{
    private readonly DeployAIDbContext _db;
    private readonly IProviderFactory _providerFactory;
    private readonly IEncryptionService _encryption;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IGitHubService _gitHubService;
    private readonly IRailwayDatabaseProvisioningService _railwayDatabaseProvisioning;
    private readonly IFrontendEnvironmentWiringService _frontendEnvironmentWiring;
    private readonly IHubContext<DeploymentHub> _hub;

    public DeploymentJobRunner(
        DeployAIDbContext db,
        IProviderFactory providerFactory,
        IEncryptionService encryption,
        IProviderCredentialTokenService tokens,
        IGitHubService gitHubService,
        IRailwayDatabaseProvisioningService railwayDatabaseProvisioning,
        IFrontendEnvironmentWiringService frontendEnvironmentWiring,
        IHubContext<DeploymentHub> hub)
    {
        _db = db;
        _providerFactory = providerFactory;
        _encryption = encryption;
        _tokens = tokens;
        _gitHubService = gitHubService;
        _railwayDatabaseProvisioning = railwayDatabaseProvisioning;
        _frontendEnvironmentWiring = frontendEnvironmentWiring;
        _hub = hub;
    }

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
                await _frontendEnvironmentWiring.WireApiUrlForWebsiteTargetAsync(
                    deployment.Id,
                    target,
                    cancellationToken);
            }

            var provider = _providerFactory.GetProvider(target.ProviderName);
            var token = await _tokens.GetTokenAsync(deployTarget.Credential, cancellationToken);
            var credentials = new ProviderCredentials(token);

            if (string.Equals(target.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
            {
                await _railwayDatabaseProvisioning.EnsureFromRepoAsync(
                    project,
                    deployTarget,
                    deployment.Branch,
                    cancellationToken);
                DetachDeployTargetChanges();
                targetConfig = DeployTargetConfig.Parse(deployTarget.ConfigJson);
            }

            var environment = new Dictionary<string, string>
            {
                ["githubRepoFullName"] = project.GitHubRepoFullName
            };
            foreach (var entry in targetConfig.ToEnvironmentEntries())
            {
                environment[entry.Key] = entry.Value;
            }

            if (string.Equals(target.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
            {
                var commitSha = await ResolveGitHubCommitShaAsync(project, deployment.Branch, cancellationToken);
                if (!string.IsNullOrWhiteSpace(commitSha))
                {
                    environment["commitSha"] = commitSha;
                }
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
                sequence++;
                await PersistAndBroadcastLogAsync(target, deployment.Id, sequence, status.ErrorMessage!, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            target.Status = DeploymentStatuses.Failed;
            target.CompletedAt = DateTimeOffset.UtcNow;
            var userMessage = ex switch
            {
                DeployAIException deployAiException => deployAiException.Message,
                _ => "Something went wrong while publishing. Try again in a moment."
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

        await _hub.Clients.Group(deploymentId.ToString())
            .SendAsync("DeploymentCompleted", deploymentId, deployment.Status, cancellationToken);
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
}
