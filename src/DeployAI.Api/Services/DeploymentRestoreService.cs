using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

public interface IDeploymentRestoreService
{
    Task<RestoreDeploymentResult> RestorePreviousAsync(Guid deploymentId, Guid userId, CancellationToken cancellationToken);
}

public sealed record RestoreDeploymentResult(
    Guid DeploymentId,
    string Status,
    IReadOnlyList<RestoreTargetResult> Targets);

public sealed record RestoreTargetResult(string ProviderName, string Status, string? Message);

public sealed class DeploymentRestoreService : IDeploymentRestoreService
{
    private readonly DeployAIDbContext _db;
    private readonly IProviderFactory _providerFactory;
    private readonly IProviderServiceOperationsFactory _serviceOperationsFactory;
    private readonly IProviderCredentialTokenService _tokens;

    public DeploymentRestoreService(
        DeployAIDbContext db,
        IProviderFactory providerFactory,
        IProviderServiceOperationsFactory serviceOperationsFactory,
        IProviderCredentialTokenService tokens)
    {
        _db = db;
        _providerFactory = providerFactory;
        _serviceOperationsFactory = serviceOperationsFactory;
        _tokens = tokens;
    }

    public async Task<RestoreDeploymentResult> RestorePreviousAsync(
        Guid deploymentId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var deployment = await _db.Deployments
            .Include(d => d.Targets)
            .Include(d => d.Project)
            .ThenInclude(p => p.DeployTargets)
            .ThenInclude(t => t.Credential)
            .FirstOrDefaultAsync(d => d.Id == deploymentId && d.Project.UserId == userId, cancellationToken);

        if (deployment is null)
        {
            throw new DeployAIException("not_found", "We couldn't find that update.");
        }

        if (deployment.Status is not DeploymentStatuses.Failed and not DeploymentStatuses.Partial)
        {
            throw new DeployAIException(
                "invalid_state",
                "Restore is only available after a failed or partial publish.");
        }

        var previousSuccessful = await _db.Deployments
            .Include(d => d.Targets)
            .Where(d =>
                d.ProjectId == deployment.ProjectId &&
                d.Id != deployment.Id &&
                d.CreatedAt < deployment.CreatedAt &&
                d.Status == DeploymentStatuses.Success)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (previousSuccessful is null)
        {
            throw new DeployAIException(
                "no_previous",
                "There isn't a previous successful version to restore.");
        }

        var results = new List<RestoreTargetResult>();
        foreach (var target in deployment.Targets.Where(t => t.Status == DeploymentStatuses.Failed))
        {
            var deployTarget = deployment.Project.DeployTargets
                .FirstOrDefault(t => t.Id == target.DeployTargetId);
            if (deployTarget?.Credential is null)
            {
                results.Add(new RestoreTargetResult(target.ProviderName, "skipped", "Missing connection."));
                continue;
            }

            var previousTarget = previousSuccessful.Targets
                .FirstOrDefault(t => t.DeployTargetId == target.DeployTargetId);
            if (previousTarget is null || string.IsNullOrWhiteSpace(previousTarget.ProviderDeploymentId))
            {
                results.Add(new RestoreTargetResult(target.ProviderName, "skipped", "No previous version found."));
                continue;
            }

            try
            {
                var token = await _tokens.GetTokenAsync(deployTarget.Credential, cancellationToken);
                var credentials = new ProviderCredentials(token);

                if (string.Equals(target.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
                {
                    var serviceOps = _serviceOperationsFactory.GetServiceOperations("railway");
                    if (serviceOps is null)
                    {
                        results.Add(new RestoreTargetResult(target.ProviderName, "skipped", "Rollback is not available."));
                        continue;
                    }

                    await serviceOps.RollbackDeploymentAsync(
                        credentials,
                        previousTarget.ProviderDeploymentId,
                        cancellationToken);
                    target.Status = DeploymentStatuses.Success;
                    target.DeployUrl = previousTarget.DeployUrl;
                    target.CompletedAt = DateTimeOffset.UtcNow;
                    results.Add(new RestoreTargetResult(target.ProviderName, "restored", null));
                    continue;
                }

                var provider = _providerFactory.GetProvider(target.ProviderName);
                await provider.TriggerDeploymentAsync(
                    credentials,
                    deployTarget.ProviderProjectId,
                    previousSuccessful.Branch,
                    new Dictionary<string, string>(),
                    cancellationToken);
                results.Add(new RestoreTargetResult(target.ProviderName, "redeployed", null));
            }
            catch (Exception ex)
            {
                var message = ex is DeployAIException deployAiException
                    ? deployAiException.Message
                    : "Could not restore this target.";
                results.Add(new RestoreTargetResult(target.ProviderName, "failed", message));
            }
        }

        var allTargetsSucceeded = deployment.Targets.All(t => t.Status == DeploymentStatuses.Success);
        deployment.Status = allTargetsSucceeded ? DeploymentStatuses.Success : DeploymentStatuses.Partial;
        deployment.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return new RestoreDeploymentResult(deployment.Id, deployment.Status, results);
    }
}
