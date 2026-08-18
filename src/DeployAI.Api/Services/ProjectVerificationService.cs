using DeployAI.Api.Services.Checks;
using DeployAI.Core.Deployments;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>Everything one project's verification concluded, before it is written down.</summary>
public sealed record ProjectVerificationResult(
    Guid ProjectId,
    Guid? DeploymentId,
    IReadOnlyList<ProjectVerificationCheck> Checks);

public interface IProjectVerificationService
{
    Task<ProjectVerificationResult> VerifyProjectAsync(Guid projectId, CancellationToken cancellationToken);
}

/// <summary>
/// Asks every check what it makes of one project.
/// </summary>
/// <remarks>
/// Composed alongside <see cref="IDeploymentVerificationService"/> rather than folded into it. That
/// service is deployment-scoped and answers what a user's "verify" button asks; these checks are
/// project-scoped — they read deploy targets, domains and credentials, not a deployment — and
/// several of them cost a provider API call, which is not something a button press should trigger on
/// every click.
/// </remarks>
public sealed class ProjectVerificationService : IProjectVerificationService
{
    private readonly DeployAIDbContext _db;
    private readonly IEnumerable<IProjectCheckContributor> _contributors;
    private readonly ILogger<ProjectVerificationService> _logger;

    public ProjectVerificationService(
        DeployAIDbContext db,
        IEnumerable<IProjectCheckContributor> contributors,
        ILogger<ProjectVerificationService> logger)
    {
        _db = db;
        _contributors = contributors;
        _logger = logger;
    }

    public async Task<ProjectVerificationResult> VerifyProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);

        var targets = await _db.DeployTargets
            .Include(t => t.Credential)
            .Where(t => t.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        var deploymentId = await ResolveDeploymentAsync(projectId, cancellationToken);
        var context = new ProjectCheckContext(project, targets, deploymentId);

        var checks = new List<ProjectVerificationCheck>();
        foreach (var contributor in _contributors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                checks.AddRange(await contributor.ContributeAsync(context, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One family of checks failing must not cost the others. Recorded as inconclusive
                // and named, because a check that vanished from the list silently would leave the
                // project looking healthier than it was measured to be.
                _logger.LogWarning(
                    ex, "Check contributor {Contributor} threw for project {ProjectId}.",
                    contributor.Name, projectId);

                checks.Add(new ProjectVerificationCheck(
                    ContributorCheckId(contributor.Name),
                    VerificationCheckTargets.Project,
                    contributor.Name,
                    VerificationCheckStatus.Inconclusive,
                    $"DeployAI could not run the {contributor.Name} checks this time ({ex.GetType().Name})."));
            }
        }

        return new ProjectVerificationResult(projectId, deploymentId, checks);
    }

    /// <summary>
    /// The check id standing in for a contributor that threw. Stable across runs, so a family of
    /// checks that keeps breaking shows up as one persistent row rather than a new one each sweep.
    /// </summary>
    public static string ContributorCheckId(string contributorName) =>
        $"contributor.{contributorName.ToLowerInvariant().Replace(' ', '_')}";

    /// <summary>
    /// Finds a deployment whose URLs are worth probing.
    /// </summary>
    /// <remarks>
    /// The ladder exists because "latest successful deployment" alone made the projects most likely
    /// to be broken invisible: a project whose last publish failed had no successful deployment to
    /// hand, so the sweep returned without recording anything at all — no status, no reason, nothing
    /// to distinguish it from a project that had never been checked. A partial deploy, or a
    /// deployment where only one side came up, still has a live address worth probing.
    /// </remarks>
    private async Task<Guid?> ResolveDeploymentAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var latestSuccess = await _db.Deployments
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Status == DeploymentStatuses.Success)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestSuccess is not null)
        {
            return latestSuccess;
        }

        var latestPartial = await _db.Deployments
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Status == DeploymentStatuses.Partial)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestPartial is not null)
        {
            return latestPartial;
        }

        // Last resort: any deployment that got at least one side of the app onto a real address.
        // DeploymentVerificationService already reports the missing half as not_deployed, so a
        // half-deployed project reads honestly rather than being skipped entirely.
        return await _db.Deployments
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId &&
                        d.Targets.Any(t => t.Status == DeploymentStatuses.Success &&
                                           t.DeployUrl != null &&
                                           t.DeployUrl != ""))
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
