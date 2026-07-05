using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IRailwayDatabaseProvisioningService _railwayDatabaseProvisioning;

    public ProjectsController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IRailwayDatabaseProvisioningService railwayDatabaseProvisioning)
    {
        _db = db;
        _currentUser = currentUser;
        _railwayDatabaseProvisioning = railwayDatabaseProvisioning;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var projects = await _db.Projects
            .Where(p => p.UserId == userId)
            .Include(p => p.DeployTargets)
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync(cancellationToken);

        var projectIds = projects.Select(p => p.Id).ToList();
        var latestDeployments = await _db.Deployments
            .Where(d => projectIds.Contains(d.ProjectId))
            .GroupBy(d => d.ProjectId)
            .Select(g => g.OrderByDescending(d => d.CreatedAt).First())
            .ToListAsync(cancellationToken);

        var latestByProject = latestDeployments.ToDictionary(d => d.ProjectId);

        return Ok(new
        {
            projects = projects.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                githubRepoFullName = p.GitHubRepoFullName,
                defaultBranch = p.DefaultBranch,
                targets = p.DeployTargets.Select(t => new { providerName = t.ProviderName }),
                latestDeployment = latestByProject.TryGetValue(p.Id, out var latest)
                    ? new
                    {
                        id = latest.Id,
                        status = latest.Status,
                        completedAt = latest.CompletedAt
                    }
                    : null
            })
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        if (request.Targets.Count == 0)
        {
            throw new DeployAIException("no_targets", "Choose where this app should live before continuing.");
        }

        var credentialIds = request.Targets.Select(t => t.CredentialId).Distinct().ToList();
        var credentials = await _db.ProviderCredentials
            .Where(c => c.UserId == userId && credentialIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        if (credentials.Count != credentialIds.Count)
        {
            throw new DeployAIException("invalid_credential", "One of your hosting connections is missing. Reconnect it in settings.");
        }

        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name,
            GitHubRepoFullName = request.GitHubRepoFullName,
            DefaultBranch = request.DefaultBranch,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        foreach (var target in request.Targets)
        {
            project.DeployTargets.Add(new DeployTarget
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                ProviderName = target.ProviderName,
                CredentialId = target.CredentialId,
                ProviderProjectId = target.ProviderProjectId,
                ConfigJson = target.Config ?? "{}",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken);

        var serverTarget = project.DeployTargets.FirstOrDefault(t =>
            string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
            !DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget);
        if (serverTarget is not null)
        {
            await _railwayDatabaseProvisioning.EnsureFromRepoAsync(
                project,
                serverTarget,
                project.DefaultBranch,
                cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Created($"/api/projects/{project.Id}", await MapProjectAsync(project.Id, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var exists = await _db.Projects.AnyAsync(p => p.Id == id && p.UserId == userId, cancellationToken);
        if (!exists)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that app." } });
        }

        return Ok(await MapProjectAsync(id, cancellationToken));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, cancellationToken);

        if (project is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that app." } });
        }

        project.Name = request.Name ?? project.Name;
        project.DefaultBranch = request.DefaultBranch ?? project.DefaultBranch;
        project.UpdatedAt = DateTimeOffset.UtcNow;

        if (request.Targets is not null)
        {
            ApplyTargetUpdates(project, request.Targets);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(await MapProjectAsync(id, cancellationToken));
    }

    [HttpPost("{id:guid}/railway-databases")]
    public async Task<IActionResult> ProvisionRailwayDatabases(
        Guid id,
        [FromBody] ProvisionRailwayDatabasesRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .ThenInclude(t => t.Credential)
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, cancellationToken);

        if (project is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that app." } });
        }

        var serverTarget = project.DeployTargets.FirstOrDefault(t =>
            string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
            !DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget);

        if (serverTarget is null)
        {
            throw new DeployAIException("no_railway_target", "Connect a Railway server before adding databases.");
        }

        await _railwayDatabaseProvisioning.ProvisionAsync(
            project,
            serverTarget,
            new DatabaseProvisioningRequest(
                request.Postgres,
                request.Redis,
                await GetPostgresDatabaseNameAsync(project, serverTarget, cancellationToken)),
            cancellationToken);

        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(await MapProjectAsync(id, cancellationToken));
    }

    [HttpPost("{id:guid}/railway-databases/auto")]
    public async Task<IActionResult> AutoProvisionRailwayDatabases(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .ThenInclude(t => t.Credential)
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, cancellationToken);

        if (project is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that app." } });
        }

        var serverTarget = project.DeployTargets.FirstOrDefault(t =>
            string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
            !DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget);

        if (serverTarget is null)
        {
            throw new DeployAIException("no_railway_target", "Connect a Railway server before adding databases.");
        }

        await _railwayDatabaseProvisioning.EnsureFromRepoAsync(
            project,
            serverTarget,
            project.DefaultBranch,
            cancellationToken);

        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(await MapProjectAsync(id, cancellationToken));
    }

    private static void ApplyTargetUpdates(Project project, IReadOnlyList<ProjectTargetRequest> targets)
    {
        var existingAppTargets = project.DeployTargets
            .Where(t => !DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget)
            .ToDictionary(t => t.ProviderName, StringComparer.OrdinalIgnoreCase);
        var updatedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            var config = DeployTargetConfig.Parse(target.Config);
            if (config.IsDatabaseTarget)
            {
                continue;
            }

            updatedProviders.Add(target.ProviderName);
            if (existingAppTargets.TryGetValue(target.ProviderName, out var existing))
            {
                existing.CredentialId = target.CredentialId;
                existing.ProviderProjectId = target.ProviderProjectId;
                existing.ConfigJson = target.Config ?? "{}";
                continue;
            }

            project.DeployTargets.Add(new DeployTarget
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                ProviderName = target.ProviderName,
                CredentialId = target.CredentialId,
                ProviderProjectId = target.ProviderProjectId,
                ConfigJson = target.Config ?? "{}",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        foreach (var removable in existingAppTargets.Values
            .Where(t => !updatedProviders.Contains(t.ProviderName))
            .ToList())
        {
            project.DeployTargets.Remove(removable);
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that app." } });
        }

        _db.Projects.Remove(project);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<string?> GetPostgresDatabaseNameAsync(
        Project project,
        DeployTarget serverTarget,
        CancellationToken cancellationToken)
    {
        var profile = await _railwayDatabaseProvisioning.DetectRequirementsAsync(
            project,
            serverTarget,
            project.DefaultBranch,
            cancellationToken);
        return profile.PostgresDatabaseName;
    }

    private async Task<object> MapProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .FirstAsync(p => p.Id == projectId, cancellationToken);

        return new
        {
            id = project.Id,
            name = project.Name,
            githubRepoFullName = project.GitHubRepoFullName,
            defaultBranch = project.DefaultBranch,
            targets = project.DeployTargets.Select(t => new
            {
                id = t.Id,
                providerName = t.ProviderName,
                credentialId = t.CredentialId,
                providerProjectId = t.ProviderProjectId,
                config = t.ConfigJson
            })
        };
    }

    private Guid RequireUserId()
    {
        return _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
    }

    public sealed record CreateProjectRequest(
        string Name,
        string GitHubRepoFullName,
        string DefaultBranch,
        List<ProjectTargetRequest> Targets,
        bool IncludePostgres = false,
        bool IncludeRedis = false);

    public sealed record UpdateProjectRequest(
        string? Name,
        string? DefaultBranch,
        List<ProjectTargetRequest>? Targets);

    public sealed record ProjectTargetRequest(
        string ProviderName,
        Guid CredentialId,
        string ProviderProjectId,
        string? Config);

    public sealed record ProvisionRailwayDatabasesRequest(bool Postgres, bool Redis);
}
