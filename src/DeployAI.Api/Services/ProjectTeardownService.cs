using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>Deletes a project and, best-effort, everything provisioned for it on each provider (app services, databases, webhooks) - not just the DeployAI database record.</summary>
public interface IProjectTeardownService
{
    /// <param name="force">Drop DeployAI's record even if provider resources could not be removed.
    /// Off by default: silently forgetting an app whose applications and databases are still
    /// running is how orphans accumulate, un-billed and untracked.</param>
    Task TeardownAsync(Guid projectId, Guid userId, bool force, CancellationToken cancellationToken);
}

public sealed class ProjectTeardownService : IProjectTeardownService
{
    private readonly DeployAIDbContext _db;
    private readonly IRailwayDatabaseProvisioningService _railwayDatabaseProvisioning;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IProviderServiceOperationsFactory _serviceOperationsFactory;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly ILogger<ProjectTeardownService> _logger;
    private readonly List<string> _failures = [];

    public ProjectTeardownService(
        DeployAIDbContext db,
        IRailwayDatabaseProvisioningService railwayDatabaseProvisioning,
        IProviderManagementFactory managementFactory,
        IProviderServiceOperationsFactory serviceOperationsFactory,
        IProviderCredentialTokenService tokens,
        ILogger<ProjectTeardownService> logger)
    {
        _db = db;
        _railwayDatabaseProvisioning = railwayDatabaseProvisioning;
        _managementFactory = managementFactory;
        _serviceOperationsFactory = serviceOperationsFactory;
        _tokens = tokens;
        _logger = logger;
    }

    public async Task TeardownAsync(Guid projectId, Guid userId, bool force, CancellationToken cancellationToken)
    {
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .ThenInclude(t => t.Credential)
            .Include(p => p.Deployments)
            .ThenInclude(d => d.Targets)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);

        if (project is null)
        {
            throw new DeployAIException("not_found", "We couldn't find that app.");
        }

        await CancelInFlightDeploymentsAsync(project, cancellationToken);
        await TeardownProviderResourcesAsync(project, cancellationToken);

        // Removing the record while its applications and databases are still up is what leaves
        // orphans on the server with nothing left in DeployAI pointing at them. Keep the app so the
        // delete can be retried, and say exactly what survived.
        if (_failures.Count > 0 && !force)
        {
            throw new DeployAIException(
                "teardown_incomplete",
                "These still exist on your server and were not removed: " +
                string.Join("; ", _failures) +
                ". Nothing was deleted from DeployAI, so you can try again once that is sorted.");
        }

        _db.Projects.Remove(project);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task CancelInFlightDeploymentsAsync(Project project, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;

        foreach (var deployment in project.Deployments)
        {
            foreach (var target in deployment.Targets)
            {
                if (target.Status is not DeploymentStatuses.Pending and not DeploymentStatuses.InProgress)
                {
                    continue;
                }

                target.Status = DeploymentStatuses.Cancelled;
                target.CompletedAt = now;
                changed = true;
            }

            if (deployment.Status is DeploymentStatuses.Pending or DeploymentStatuses.InProgress)
            {
                deployment.Status = DeploymentStatuses.Cancelled;
                deployment.CompletedAt = now;
                changed = true;
            }
        }

        if (changed)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task TeardownProviderResourcesAsync(Project project, CancellationToken cancellationToken)
    {
        var railwayServerTarget = project.DeployTargets.FirstOrDefault(t =>
            string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
            !DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget);

        var railwayDatabaseTargets = project.DeployTargets
            .Where(t =>
                string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
                DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget)
            .ToList();

        foreach (var databaseTarget in railwayDatabaseTargets)
        {
            await TryProviderStepAsync(
                $"Railway database service {databaseTarget.Id}",
                () => _railwayDatabaseProvisioning.TeardownDatabaseServiceOnProviderAsync(
                    project,
                    databaseTarget,
                    cancellationToken));
        }

        if (railwayServerTarget is not null &&
            !string.IsNullOrWhiteSpace(railwayServerTarget.ProviderProjectId))
        {
            await TryProviderStepAsync(
                $"Railway server service {railwayServerTarget.ProviderProjectId}",
                async () =>
                {
                    var credentials = await GetCredentialsAsync(railwayServerTarget, cancellationToken);
                    var serviceOperations = _serviceOperationsFactory.GetServiceOperations(railwayServerTarget.ProviderName);
                    if (serviceOperations is null)
                    {
                        return;
                    }

                    await serviceOperations.DeleteServiceAsync(
                        credentials,
                        railwayServerTarget.ProviderProjectId,
                        cancellationToken);
                });
        }

        var railwayProjectId = ResolveRailwayProjectId(project, railwayServerTarget, railwayDatabaseTargets);
        if (!string.IsNullOrWhiteSpace(railwayProjectId) && railwayServerTarget is not null)
        {
            await TryProviderStepAsync(
                $"Railway project {railwayProjectId}",
                async () =>
                {
                    var credentials = await GetCredentialsAsync(railwayServerTarget, cancellationToken);
                    var management = _managementFactory.GetManagement(railwayServerTarget.ProviderName);
                    await management.DeleteProjectAsync(credentials, railwayProjectId, cancellationToken);
                });
        }

        var vercelTarget = project.DeployTargets.FirstOrDefault(t =>
            string.Equals(t.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase));

        if (vercelTarget is not null &&
            !string.IsNullOrWhiteSpace(vercelTarget.ProviderProjectId))
        {
            await TryProviderStepAsync(
                $"Vercel project {vercelTarget.ProviderProjectId}",
                async () =>
                {
                    var credentials = await GetCredentialsAsync(vercelTarget, cancellationToken);
                    var management = _managementFactory.GetManagement(vercelTarget.ProviderName);
                    await management.DeleteProjectAsync(
                        credentials,
                        vercelTarget.ProviderProjectId,
                        cancellationToken);
                });
        }

        var coolifyDatabaseTargets = project.DeployTargets
            .Where(t =>
                string.Equals(t.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
                DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget)
            .ToList();

        foreach (var databaseTarget in coolifyDatabaseTargets)
        {
            await TryProviderStepAsync(
                $"Coolify database {databaseTarget.ProviderProjectId}",
                () => _railwayDatabaseProvisioning.TeardownDatabaseServiceOnProviderAsync(
                    project,
                    databaseTarget,
                    cancellationToken));
        }

        var coolifyAppTargets = project.DeployTargets
            .Where(t =>
                string.Equals(t.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase) &&
                !DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget)
            .ToList();

        foreach (var appTarget in coolifyAppTargets)
        {
            if (string.IsNullOrWhiteSpace(appTarget.ProviderProjectId))
            {
                continue;
            }

            await TryProviderStepAsync(
                $"Coolify application {appTarget.ProviderProjectId}",
                async () =>
                {
                    var credentials = await GetCredentialsAsync(appTarget, cancellationToken);
                    var serviceOperations = _serviceOperationsFactory.GetServiceOperations(ProviderNameValues.Coolify);
                    if (serviceOperations is null)
                    {
                        return;
                    }

                    await serviceOperations.DeleteServiceAsync(
                        credentials,
                        appTarget.ProviderProjectId,
                        cancellationToken);
                });
        }
    }

    private static string? ResolveRailwayProjectId(
        Project project,
        DeployTarget? railwayServerTarget,
        IReadOnlyList<DeployTarget> railwayDatabaseTargets)
    {
        if (railwayServerTarget is not null)
        {
            var serverConfig = DeployTargetConfig.Parse(railwayServerTarget.ConfigJson);
            if (!string.IsNullOrWhiteSpace(serverConfig.RailwayProjectId))
            {
                return serverConfig.RailwayProjectId;
            }
        }

        foreach (var databaseTarget in railwayDatabaseTargets)
        {
            var config = DeployTargetConfig.Parse(databaseTarget.ConfigJson);
            if (!string.IsNullOrWhiteSpace(config.RailwayProjectId))
            {
                return config.RailwayProjectId;
            }
        }

        return null;
    }

    private async Task<ProviderCredentials> GetCredentialsAsync(
        DeployTarget target,
        CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync(target.Credential, cancellationToken);
        return new ProviderCredentials(token);
    }

    /// <summary>
    /// Every resource is attempted even when an earlier one fails — stopping at the first error
    /// would strand the rest — but failures are collected so the caller can refuse to forget an
    /// app whose resources are still running.
    /// </summary>
    private async Task TryProviderStepAsync(string description, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider teardown step failed: {Description}", description);
            _failures.Add($"{description} ({ex.Message})");
        }
    }
}
