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
[Route("api/projects/{projectId:guid}/services")]
public sealed class ProjectServicesController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IRailwayDatabaseProvisioningService _railwayDatabaseProvisioning;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IProviderServiceOperationsFactory _serviceOperationsFactory;
    private readonly IProviderRuntimeLogsFactory _runtimeLogsFactory;
    private readonly IProviderLifecycleOperationsFactory _lifecycleFactory;
    private readonly IDataServiceInspectionService _dataServiceInspection;
    private readonly IFrontendEnvironmentWiringService _frontendEnvironmentWiring;

    public ProjectServicesController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IRailwayDatabaseProvisioningService railwayDatabaseProvisioning,
        IProviderCredentialTokenService tokens,
        IProviderServiceOperationsFactory serviceOperationsFactory,
        IProviderRuntimeLogsFactory runtimeLogsFactory,
        IProviderLifecycleOperationsFactory lifecycleFactory,
        IDataServiceInspectionService dataServiceInspection,
        IFrontendEnvironmentWiringService frontendEnvironmentWiring)
    {
        _db = db;
        _currentUser = currentUser;
        _railwayDatabaseProvisioning = railwayDatabaseProvisioning;
        _tokens = tokens;
        _serviceOperationsFactory = serviceOperationsFactory;
        _runtimeLogsFactory = runtimeLogsFactory;
        _lifecycleFactory = lifecycleFactory;
        _dataServiceInspection = dataServiceInspection;
        _frontendEnvironmentWiring = frontendEnvironmentWiring;
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await GetOwnedProjectAsync(projectId, cancellationToken);
        return Ok(MapServicesResponse(project));
    }

    [HttpGet("{targetId:guid}/status")]
    public async Task<IActionResult> GetStatus(
        Guid projectId,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var project = await GetOwnedProjectAsync(projectId, cancellationToken);
        var target = GetTarget(project, targetId);
        var operations = GetServiceOperations(target);

        var token = await _tokens.GetTokenAsync(target.Credential, cancellationToken);
        var status = await operations.GetServiceStatusAsync(
            new ProviderCredentials(token),
            target.ProviderProjectId,
            cancellationToken);

        return Ok(new
        {
            status = status.Status,
            deployUrl = status.DeployUrl,
            lastDeployedAt = status.LastDeployedAt
        });
    }

    /// <summary>
    /// Runtime container output (docker logs), the missing piece when an app builds fine
    /// but its container crash-loops — build logs and status can't say why, stdout can.
    /// </summary>
    [HttpGet("{targetId:guid}/runtime-logs")]
    public async Task<IActionResult> GetRuntimeLogs(
        Guid projectId,
        Guid targetId,
        [FromQuery] int lines = 200,
        CancellationToken cancellationToken = default)
    {
        var project = await GetOwnedProjectAsync(projectId, cancellationToken);
        var target = GetTarget(project, targetId);
        var runtimeLogs = _runtimeLogsFactory.GetRuntimeLogs(target.ProviderName)
            ?? throw new DeployAIException("unsupported_provider", "Runtime logs are not available for this provider.");

        var token = await _tokens.GetTokenAsync(target.Credential, cancellationToken);
        var logs = await runtimeLogs.GetRuntimeLogsAsync(
            new ProviderCredentials(token),
            target.ProviderProjectId,
            lines,
            cancellationToken);

        return Ok(new { logs });
    }

    /// <summary>
    /// Start/stop/restart the deployed containers without rebuilding — a crash-looped
    /// stack that hit its restart limit needs a start, not a full redeploy.
    /// </summary>
    [HttpPost("{targetId:guid}/lifecycle/{operation}")]
    public async Task<IActionResult> Lifecycle(
        Guid projectId,
        Guid targetId,
        string operation,
        CancellationToken cancellationToken)
    {
        var project = await GetOwnedProjectAsync(projectId, cancellationToken);
        var target = GetTarget(project, targetId);
        var lifecycle = _lifecycleFactory.GetLifecycleOperations(target.ProviderName)
            ?? throw new DeployAIException("unsupported_provider", "Start/stop/restart is not available for this provider.");

        var token = await _tokens.GetTokenAsync(target.Credential, cancellationToken);
        var credentials = new ProviderCredentials(token);
        Task pending = operation.ToLowerInvariant() switch
        {
            "start" => lifecycle.StartApplicationAsync(credentials, target.ProviderProjectId, cancellationToken),
            "stop" => lifecycle.StopApplicationAsync(credentials, target.ProviderProjectId, cancellationToken),
            "restart" => lifecycle.RestartApplicationAsync(credentials, target.ProviderProjectId, cancellationToken),
            _ => throw new DeployAIException("invalid_action", "Use start, stop, or restart.")
        };
        await pending;

        return Ok(new { message = $"Requested {operation.ToLowerInvariant()}." });
    }

    [HttpPost("{targetId:guid}/redeploy")]
    public async Task<IActionResult> Redeploy(
        Guid projectId,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var project = await GetOwnedProjectAsync(projectId, cancellationToken);
        var target = GetTarget(project, targetId);

        if (string.Equals(target.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
        {
            await _frontendEnvironmentWiring.SyncCrossProviderEnvironmentAsync(
                projectId,
                new EnvironmentSyncOptions(
                    RedeployRailwayAfterUpdate: false,
                    EnsureWebsiteWiring: true,
                    ApplyVercelEnv: true,
                    ApplyRailwayEnv: true,
                    RunVerification: false,
                    Source: "manual"),
                cancellationToken);
        }

        var operations = GetServiceOperations(target);

        var token = await _tokens.GetTokenAsync(target.Credential, cancellationToken);
        await operations.RedeployServiceAsync(
            new ProviderCredentials(token),
            target.ProviderProjectId,
            cancellationToken);

        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { message = "Service restart requested." });
    }

    [HttpDelete("{targetId:guid}")]
    public async Task<IActionResult> Remove(
        Guid projectId,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var project = await GetOwnedProjectAsync(projectId, cancellationToken);
        var target = GetTarget(project, targetId);
        var config = DeployTargetConfig.Parse(target.ConfigJson);

        if (!config.IsDatabaseTarget)
        {
            throw new DeployAIException("invalid_target", "Only data services can be removed from DeployAI.");
        }

        await _railwayDatabaseProvisioning.RemoveDatabaseServiceAsync(project, target, cancellationToken);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(MapServicesResponse(project));
    }

    [HttpGet("{targetId:guid}/data-info")]
    public async Task<IActionResult> GetDataInfo(
        Guid projectId,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var project = await GetOwnedProjectAsync(projectId, cancellationToken);
        var target = GetTarget(project, targetId);
        var config = DeployTargetConfig.Parse(target.ConfigJson);

        if (!config.IsDatabaseTarget)
        {
            throw new DeployAIException("invalid_target", "Only data services expose database information.");
        }

        var token = await _tokens.GetTokenAsync(target.Credential, cancellationToken);
        var info = await _dataServiceInspection.GetDataServiceInfoAsync(
            target,
            new ProviderCredentials(token),
            cancellationToken);

        return Ok(info);
    }

    private static object MapServicesResponse(Project project)
    {
        var services = ProjectServiceMapper.MapProjectServices(project);
        return new
        {
            applicationServices = services.ApplicationServices.Select(MapService),
            dataServices = services.DataServices.Select(MapService),
            hasRailwayServer = services.HasRailwayServer,
            includePostgres = services.IncludePostgres,
            includeRedis = services.IncludeRedis
        };
    }

    private static object MapService(ProjectServiceView service) => new
    {
        id = service.Id,
        providerName = service.ProviderName,
        credentialId = service.CredentialId,
        providerProjectId = service.ProviderProjectId,
        role = service.Role,
        databaseEngine = service.DatabaseEngine,
        displayName = service.DisplayName,
        railwayProjectId = service.RailwayProjectId,
        linkedConnectionKeys = service.LinkedConnectionKeys,
        canManage = service.CanManage,
        serviceDirectory = service.ServiceDirectory,
        rootDirectory = service.RootDirectory
    };

    private async Task<Project> GetOwnedProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .ThenInclude(t => t.Credential)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);

        return project ?? throw new DeployAIException("not_found", "We couldn't find that app.");
    }

    private static DeployTarget GetTarget(Project project, Guid targetId)
    {
        return project.DeployTargets.FirstOrDefault(t => t.Id == targetId)
            ?? throw new DeployAIException("not_found", "We couldn't find that service.");
    }

    private IProviderServiceOperations GetServiceOperations(DeployTarget target)
    {
        return _serviceOperationsFactory.GetServiceOperations(target.ProviderName)
            ?? throw new DeployAIException("unsupported_provider", "Service operations are not available for this provider.");
    }

    private Guid RequireUserId() =>
        _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
}
