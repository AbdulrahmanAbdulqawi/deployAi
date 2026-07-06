using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

public interface IProjectTeardownService
{
    Task TeardownAsync(Guid projectId, Guid userId, CancellationToken cancellationToken);
}

public sealed class ProjectTeardownService : IProjectTeardownService
{
    private readonly DeployAIDbContext _db;
    private readonly IRailwayDatabaseProvisioningService _railwayDatabaseProvisioning;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IProviderServiceOperationsFactory _serviceOperationsFactory;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly ILogger<ProjectTeardownService> _logger;

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

    public async Task TeardownAsync(Guid projectId, Guid userId, CancellationToken cancellationToken)
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

    private async Task TryProviderStepAsync(string description, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider teardown step failed: {Description}", description);
        }
    }
}
