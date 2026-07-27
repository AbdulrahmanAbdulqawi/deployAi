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

/// <summary>
/// Manages a user's deploy projects: creation (from explicit targets or a classified deployment
/// plan), reading, updating deploy target config, health, Railway database provisioning, branch
/// switching, and teardown.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IRailwayDatabaseProvisioningService _railwayDatabaseProvisioning;
    private readonly IProjectTeardownService _projectTeardown;
    private readonly IProjectBranchDeployService _branchDeployService;
    private readonly IGitHubWebhookRegistrationService _webhookRegistration;

    public ProjectsController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IRailwayDatabaseProvisioningService railwayDatabaseProvisioning,
        IProjectTeardownService projectTeardown,
        IProjectBranchDeployService branchDeployService,
        IGitHubWebhookRegistrationService webhookRegistration)
    {
        _db = db;
        _currentUser = currentUser;
        _railwayDatabaseProvisioning = railwayDatabaseProvisioning;
        _projectTeardown = projectTeardown;
        _branchDeployService = branchDeployService;
        _webhookRegistration = webhookRegistration;
    }

    /// <summary>Lists the current user's projects with each one's latest deployment status.</summary>
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

        var latestDeploymentIds = latestDeployments.Select(d => d.Id).ToList();
        var latestTargets = latestDeploymentIds.Count == 0
            ? []
            : await _db.DeploymentTargets
                .Where(t => latestDeploymentIds.Contains(t.DeploymentId))
                .ToListAsync(cancellationToken);

        var targetsByDeployment = latestTargets
            .GroupBy(t => t.DeploymentId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Data.Entities.DeploymentTarget>)g.ToList());

        return Ok(new
        {
            projects = projects.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                logoKey = p.LogoKey,
                githubRepoFullName = p.GitHubRepoFullName,
                defaultBranch = p.DefaultBranch,
                targets = p.DeployTargets
                    .Where(t => DeployTargetConfig.Parse(t.ConfigJson).IsDeployableTarget)
                    .GroupBy(t => t.ProviderName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { providerName = g.Key }),
                latestDeployment = latestByProject.TryGetValue(p.Id, out var latest)
                    ? MapLatestDeployment(latest, targetsByDeployment)
                    : null
            })
        });
    }

    private static object MapLatestDeployment(
        Data.Entities.Deployment latest,
        IReadOnlyDictionary<Guid, IReadOnlyList<Data.Entities.DeploymentTarget>> targetsByDeployment)
    {
        var (canRequestClaudeFix, fixTargetId) = targetsByDeployment.TryGetValue(latest.Id, out var targets)
            ? GetFixInfo(targets)
            : (false, (Guid?)null);

        return new
        {
            id = latest.Id,
            status = latest.Status,
            completedAt = latest.CompletedAt,
            canRequestClaudeFix,
            fixTargetId
        };
    }

    private static (bool CanRequestClaudeFix, Guid? FixTargetId) GetFixInfo(
        IReadOnlyList<Data.Entities.DeploymentTarget> targets)
    {
        foreach (var target in targets)
        {
            if (!string.Equals(target.Status, DeploymentStatuses.Failed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var analysis = DeploymentFailureAnalysisJson.Parse(target.FailureAnalysisJson);
            if (analysis?.CanRequestClaudeFix == true)
            {
                return (true, target.Id);
            }
        }

        return (false, null);
    }

    /// <summary>
    /// Creates a project from explicit deploy targets (already-created provider projects/apps).
    /// Fails with <c>project_already_exists</c> if the user already has a project for this repo.
    /// If a Railway/Coolify server target is included, also detects and provisions any databases
    /// the repo needs as a side effect.
    /// </summary>
    /// <param name="request">Name, repo, default branch, and the deploy targets to attach.</param>
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

        var normalizedRepo = request.GitHubRepoFullName.Trim();
        var existingProject = await _db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.UserId == userId &&
                     p.GitHubRepoFullName == normalizedRepo,
                cancellationToken);
        if (existingProject is not null)
        {
            throw new DeployAIException(
                "project_already_exists",
                "You already have an app for this GitHub repo. Open the existing app instead of creating another one.");
        }

        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name,
            LogoKey = NormalizeLogoKey(request.LogoKey),
            GitHubRepoFullName = normalizedRepo,
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
        {
            var config = DeployTargetConfig.Parse(t.ConfigJson);
            return config.IsDeployableTarget &&
                   string.Equals(config.Role, "server", StringComparison.OrdinalIgnoreCase) &&
                   (string.Equals(t.ProviderName, ProviderNameValues.Railway, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase));
        });
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

    /// <summary>
    /// Creates a project from a classified deployment plan (as returned by
    /// <c>GitHubController.GetDeploymentPlan</c>) instead of explicit targets - convenience wrapper
    /// around <see cref="Create"/> that converts plan parts into deploy targets, dropping any
    /// database part (databases are provisioned separately, not passed as a target).
    /// </summary>
    /// <param name="request">The classified plan parts plus name/repo/branch and database inclusion flags.</param>
    [HttpPost("from-plan")]
    public async Task<IActionResult> CreateFromPlan(
        [FromBody] CreateProjectFromPlanRequest request,
        CancellationToken cancellationToken)
    {
        var targets = request.Parts
            .Where(part => !string.Equals(part.Role, "database", StringComparison.OrdinalIgnoreCase))
            .Select(part => new ProjectTargetRequest(
                part.ProviderName,
                part.CredentialId,
                part.ProviderProjectId,
                BuildConfigFromPlanPart(part)))
            .ToList();

        var createRequest = new CreateProjectRequest(
            request.Name,
            request.GitHubRepoFullName,
            request.DefaultBranch,
            targets,
            request.IncludePostgres ?? false,
            request.IncludeRedis ?? false,
            request.LogoKey);

        return await Create(createRequest, cancellationToken);
    }

    /// <summary>Gets a single project, including its deploy targets and latest deployment.</summary>
    /// <param name="id">The project to fetch (must be owned by the current user).</param>
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

    /// <summary>
    /// Updates project fields and/or deploy target config. Target config changes are saved to
    /// DeployAI's own record immediately, but for Coolify targets only take effect on the
    /// provider's side at the next deploy (pushed automatically then), not instantly here.
    /// </summary>
    /// <param name="id">The project to update.</param>
    /// <param name="request">Fields to change; omit a field/leave targets null to leave it as-is.</param>
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
        if (request.LogoKey is not null)
        {
            project.LogoKey = NormalizeLogoKey(request.LogoKey);
        }
        project.DefaultBranch = request.DefaultBranch ?? project.DefaultBranch;
        project.UpdatedAt = DateTimeOffset.UtcNow;

        if (request.Targets is not null)
        {
            ApplyTargetUpdates(project, request.Targets);
        }

        if (request.AutoDeployEnabled.HasValue)
        {
            await _webhookRegistration.SetAutoDeployAsync(project, request.AutoDeployEnabled.Value, cancellationToken);
        }

        project.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(await MapProjectAsync(id, cancellationToken));
    }

    /// <summary>Gets the most recently recorded health-check summary for a project, if any has run.</summary>
    /// <param name="id">The project to check.</param>
    [HttpGet("{id:guid}/health")]
    public async Task<IActionResult> GetHealth(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var project = await _db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, cancellationToken);

        if (project is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that app." } });
        }

        var health = ProjectHealthState.Parse(project.HealthJson);
        if (health is null)
        {
            return Ok(new { hasHealthCheck = false });
        }

        return Ok(new
        {
            hasHealthCheck = true,
            lastCheckedAt = health.LastCheckedAt,
            status = health.Status.ToString().ToLowerInvariant(),
            passedChecks = health.PassedChecks,
            totalChecks = health.TotalChecks,
            summary = health.Summary,
            deploymentId = health.DeploymentId
        });
    }

    /// <summary>
    /// Explicitly provisions Postgres and/or Redis on a project's Railway server, wiring the
    /// resulting connection string(s) as env vars. Requires an existing Railway server target.
    /// </summary>
    /// <param name="id">The project to provision databases for.</param>
    /// <param name="request">Which database engines to provision.</param>
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

    /// <summary>
    /// Detects which databases a project's repo needs (from docker-compose/appsettings/Prisma) and
    /// provisions exactly those on Railway automatically, rather than the caller choosing engines
    /// explicitly like <see cref="ProvisionRailwayDatabases"/>.
    /// </summary>
    /// <param name="id">The project to auto-provision databases for.</param>
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

    private static string TargetKey(string providerName, string? role) =>
        $"{providerName.Trim().ToLowerInvariant()}|{role?.Trim().ToLowerInvariant()}";

    private static void ApplyTargetUpdates(Project project, IReadOnlyList<ProjectTargetRequest> targets)
    {
        // Keyed by provider+role (not just provider) so a project can hold more than one
        // target per provider - e.g. a Coolify full-stack app has separate website and
        // server targets that both use providerName "coolify".
        var existingAppTargets = project.DeployTargets
            .Where(t => !DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget)
            .ToDictionary(t => TargetKey(t.ProviderName, DeployTargetConfig.Parse(t.ConfigJson).Role));
        var updatedKeys = new HashSet<string>();

        foreach (var target in targets)
        {
            var config = DeployTargetConfig.Parse(target.Config);
            if (config.IsDatabaseTarget)
            {
                continue;
            }

            var key = TargetKey(target.ProviderName, config.Role);
            updatedKeys.Add(key);
            if (existingAppTargets.TryGetValue(key, out var existing))
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

        foreach (var removable in existingAppTargets
            .Where(kvp => !updatedKeys.Contains(kvp.Key))
            .Select(kvp => kvp.Value)
            .ToList())
        {
            project.DeployTargets.Remove(removable);
        }
    }

    /// <summary>
    /// Switches a project's default branch and, optionally, immediately triggers a deployment from it.
    /// </summary>
    /// <param name="id">The project to update.</param>
    /// <param name="request">The branch to switch to and whether to deploy it now.</param>
    [HttpPost("{id:guid}/use-branch-and-deploy")]
    public async Task<IActionResult> UseBranchAndDeploy(
        Guid id,
        [FromBody] UseBranchAndDeployRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var result = await _branchDeployService.UseBranchAndDeployAsync(
            userId,
            id,
            request.Branch,
            request.Deploy,
            cancellationToken);

        return Ok(new
        {
            branch = result.Branch,
            deploymentId = result.DeploymentId,
            message = result.Message
        });
    }

    /// <summary>
    /// Deletes a project and tears down its provider-side resources too (deploy targets,
    /// databases, webhooks) - not just the DeployAI record.
    /// </summary>
    /// <param name="id">The project to delete.</param>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        await _projectTeardown.TeardownAsync(id, userId, cancellationToken);
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
            logoKey = project.LogoKey,
            githubRepoFullName = project.GitHubRepoFullName,
            defaultBranch = project.DefaultBranch,
            autoDeployEnabled = project.AutoDeployEnabled,
            health = MapHealth(project.HealthJson),
            environmentSync = MapEnvironmentSync(project.EnvironmentSyncJson),
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

    private static object? MapHealth(string? json)
    {
        var health = ProjectHealthState.Parse(json);
        if (health is null)
        {
            return null;
        }

        return new
        {
            lastCheckedAt = health.LastCheckedAt,
            status = health.Status.ToString().ToLowerInvariant(),
            passedChecks = health.PassedChecks,
            totalChecks = health.TotalChecks,
            summary = health.Summary,
            deploymentId = health.DeploymentId
        };
    }

    private static object? MapEnvironmentSync(string? json)
    {
        var state = ProjectEnvironmentSyncState.Parse(json);
        if (state is null)
        {
            return null;
        }

        return new
        {
            lastSyncedAt = state.LastSyncedAt,
            source = state.Source,
            success = state.Success,
            driftDetected = state.DriftDetected,
            resolvedWebsiteUrl = state.ResolvedWebsiteUrl,
            resolvedApiUrl = state.ResolvedApiUrl,
            verificationMessages = state.VerificationMessages,
            driftDetails = state.DriftDetails
        };
    }

    private Guid RequireUserId()
    {
        return _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
    }

    private static string BuildConfigFromPlanPart(PlanPartTargetRequest part)
    {
        if (string.Equals(part.Role, "website", StringComparison.OrdinalIgnoreCase))
        {
            return DeployTargetConfig.FromProfile(
                new FrontendBuildProfile(
                    part.RootDirectory ?? string.Empty,
                    part.BuildCommand ?? string.Empty,
                    part.InstallCommand ?? string.Empty,
                    part.OutputDirectory ?? string.Empty,
                    part.Framework),
                "website").ToJson();
        }

        return DeployTargetConfig.FromServerProfile(
            new ServerBuildProfile(
                part.RootDirectory ?? string.Empty,
                part.BuildCommand,
                part.InstallCommand,
                part.StartCommand,
                part.Framework,
                part.DockerfilePath,
                part.ServiceDirectory ?? part.RootDirectory),
            "server").ToJson();
    }

    private static string? NormalizeLogoKey(string? logoKey)
    {
        if (string.IsNullOrWhiteSpace(logoKey))
        {
            return null;
        }

        var normalized = logoKey.Trim().ToLowerInvariant();
        return normalized.Length > 32 ? normalized[..32] : normalized;
    }

    public sealed record CreateProjectRequest(
        string Name,
        string GitHubRepoFullName,
        string DefaultBranch,
        List<ProjectTargetRequest> Targets,
        bool IncludePostgres = false,
        bool IncludeRedis = false,
        string? LogoKey = null);

    public sealed record CreateProjectFromPlanRequest(
        string Name,
        string GitHubRepoFullName,
        string DefaultBranch,
        List<PlanPartTargetRequest> Parts,
        bool? IncludePostgres = null,
        bool? IncludeRedis = null,
        string? LogoKey = null);

    public sealed record PlanPartTargetRequest(
        string Role,
        string ProviderName,
        Guid CredentialId,
        string ProviderProjectId,
        string? RootDirectory = null,
        string? ServiceDirectory = null,
        string? BuildCommand = null,
        string? InstallCommand = null,
        string? StartCommand = null,
        string? OutputDirectory = null,
        string? Framework = null,
        string? DockerfilePath = null);

    public sealed record UpdateProjectRequest(
        string? Name,
        string? DefaultBranch,
        List<ProjectTargetRequest>? Targets,
        string? LogoKey = null,
        bool? AutoDeployEnabled = null);

    public sealed record ProjectTargetRequest(
        string ProviderName,
        Guid CredentialId,
        string ProviderProjectId,
        string? Config);

    public sealed record ProvisionRailwayDatabasesRequest(bool Postgres, bool Redis);

    public sealed record UseBranchAndDeployRequest(string Branch, bool Deploy = true);
}
