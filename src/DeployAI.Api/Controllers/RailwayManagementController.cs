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
[Route("api/credentials")]
public sealed class RailwayManagementController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IRailwayDatabaseProvisioningService _railwayDatabaseProvisioning;
    private readonly IProviderCredentialTokenService _tokens;

    public RailwayManagementController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IProviderManagementFactory managementFactory,
        IRailwayDatabaseProvisioningService railwayDatabaseProvisioning,
        IProviderCredentialTokenService tokens)
    {
        _db = db;
        _currentUser = currentUser;
        _managementFactory = managementFactory;
        _railwayDatabaseProvisioning = railwayDatabaseProvisioning;
        _tokens = tokens;
    }

    [HttpPost("railway/projects")]
    public async Task<IActionResult> CreateRailwayProject(
        [FromBody] CreateRailwayProjectRequest request,
        CancellationToken cancellationToken)
    {
        var credential = await GetRailwayCredentialAsync(request.CredentialId, cancellationToken);
        var management = _managementFactory.GetManagement("railway");
        var token = await _tokens.GetTokenAsync(credential, cancellationToken);

        var project = await management.CreateProjectAsync(
            new ProviderCredentials(token),
            new CreateProviderProjectRequest(
                request.Name,
                request.GitHubRepoFullName,
                request.Framework,
                request.RootDirectory,
                null,
                request.BuildCommand,
                request.InstallCommand,
                request.DockerfilePath,
                request.ServiceDirectory,
                request.StartCommand),
            cancellationToken);

        return Ok(new { project });
    }

    [HttpPost("railway/projects/{providerProjectId}/databases")]
    public async Task<IActionResult> ProvisionDatabases(
        string providerProjectId,
        [FromBody] ProvisionRailwayDatabasesRequest request,
        CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
        var decodedProjectId = Uri.UnescapeDataString(providerProjectId);

        var deployTarget = await _db.DeployTargets
            .Include(t => t.Credential)
            .Include(t => t.Project)
            .ThenInclude(p => p.DeployTargets)
            .FirstOrDefaultAsync(
                t => t.ProviderProjectId == decodedProjectId &&
                     t.Project.UserId == userId &&
                     !DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget,
                cancellationToken);

        if (deployTarget is null)
        {
            throw new DeployAIException("not_found", "We couldn't find that Railway service.");
        }

        if (!string.Equals(deployTarget.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeployAIException("invalid_target", "Database provisioning is only supported for Railway.");
        }

        var profile = await _railwayDatabaseProvisioning.DetectRequirementsAsync(
            deployTarget.Project,
            deployTarget,
            deployTarget.Project.DefaultBranch,
            cancellationToken);

        await _railwayDatabaseProvisioning.ProvisionAsync(
            deployTarget.Project,
            deployTarget,
            new DatabaseProvisioningRequest(
                request.Postgres,
                request.Redis,
                profile.PostgresDatabaseName),
            cancellationToken);

        deployTarget.Project.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var services = ProjectServiceMapper.MapProjectServices(deployTarget.Project);
        return Ok(new
        {
            postgres = services.DataServices.FirstOrDefault(s =>
                string.Equals(s.DatabaseEngine, "postgres", StringComparison.OrdinalIgnoreCase)),
            redis = services.DataServices.FirstOrDefault(s =>
                string.Equals(s.DatabaseEngine, "redis", StringComparison.OrdinalIgnoreCase))
        });
    }

    private async Task<ProviderCredential> GetRailwayCredentialAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
        var credential = await _db.ProviderCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId, cancellationToken);

        if (credential is null)
        {
            throw new DeployAIException("not_found", "We couldn't find that connection.");
        }

        if (!string.Equals(credential.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeployAIException("invalid_credential", "This connection is not a Railway connection.");
        }

        return credential;
    }

    public sealed record CreateRailwayProjectRequest(
        Guid CredentialId,
        string Name,
        string GitHubRepoFullName,
        string? Framework,
        string? RootDirectory,
        string? BuildCommand,
        string? InstallCommand,
        string? DockerfilePath,
        string? ServiceDirectory,
        string? StartCommand);

    public sealed record ProvisionRailwayDatabasesRequest(bool Postgres, bool Redis);
}
