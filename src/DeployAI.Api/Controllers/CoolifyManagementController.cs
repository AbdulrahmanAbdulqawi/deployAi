using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Providers.Coolify;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/credentials")]
public sealed class CoolifyManagementController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly CoolifyProvider _coolifyProvider;
    private readonly IEncryptionService _encryption;

    public CoolifyManagementController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        CoolifyProvider coolifyProvider,
        IEncryptionService encryption)
    {
        _db = db;
        _currentUser = currentUser;
        _coolifyProvider = coolifyProvider;
        _encryption = encryption;
    }

    [HttpGet("{credentialId:guid}/coolify/infrastructure")]
    public async Task<IActionResult> ListInfrastructure(
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        var credential = await GetCoolifyCredentialAsync(credentialId, cancellationToken);
        var token = _encryption.Decrypt(credential.TokenEncrypted);
        var infrastructure = await _coolifyProvider.ListInfrastructureAsync(
            new ProviderCredentials(token),
            cancellationToken);

        return Ok(new
        {
            projects = infrastructure.Projects,
            servers = infrastructure.Servers,
            githubApps = infrastructure.GithubApps
        });
    }

    [HttpGet("{credentialId:guid}/coolify/projects/{projectUuid}/environments")]
    public async Task<IActionResult> ListProjectEnvironments(
        Guid credentialId,
        string projectUuid,
        CancellationToken cancellationToken)
    {
        var credential = await GetCoolifyCredentialAsync(credentialId, cancellationToken);
        var token = _encryption.Decrypt(credential.TokenEncrypted);
        var environments = await _coolifyProvider.ListProjectEnvironmentsAsync(
            new ProviderCredentials(token),
            projectUuid,
            cancellationToken);

        return Ok(new { environments });
    }

    [HttpPost("coolify/projects")]
    public async Task<IActionResult> CreateCoolifyProject(
        [FromBody] CreateCoolifyProjectRequest request,
        CancellationToken cancellationToken)
    {
        var credential = await GetCoolifyCredentialAsync(request.CredentialId, cancellationToken);
        var token = _encryption.Decrypt(credential.TokenEncrypted);

        var project = await _coolifyProvider.CreateProjectAsync(
            new ProviderCredentials(token),
            new CreateProviderProjectRequest(
                request.Name,
                request.GitHubRepoFullName,
                request.Framework,
                request.RootDirectory,
                request.OutputDirectory,
                request.BuildCommand,
                request.InstallCommand,
                request.DockerfilePath,
                request.ServiceDirectory,
                request.StartCommand,
                request.GitBranch,
                request.IsPrivateRepository,
                request.CoolifyProjectUuid,
                request.CoolifyServerUuid,
                request.CoolifyEnvironmentName,
                request.CoolifyGithubAppUuid,
                request.BuildPack,
                request.ComposeFileLocation,
                request.CustomDomain,
                request.DomainServiceName),
            cancellationToken);

        return Ok(new { project });
    }

    private async Task<ProviderCredential> GetCoolifyCredentialAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
        var credential = await _db.ProviderCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId, cancellationToken);

        if (credential is null)
        {
            throw new DeployAIException("not_found", "We couldn't find that connection.");
        }

        if (!string.Equals(credential.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeployAIException("invalid_credential", "This connection is not a Coolify connection.");
        }

        return credential;
    }

    public sealed record CreateCoolifyProjectRequest(
        Guid CredentialId,
        string Name,
        string GitHubRepoFullName,
        string GitBranch,
        bool IsPrivateRepository,
        string? Framework,
        string? RootDirectory,
        string? OutputDirectory,
        string? BuildCommand,
        string? InstallCommand,
        string? DockerfilePath,
        string? ServiceDirectory,
        string? StartCommand,
        string? CoolifyProjectUuid,
        string? CoolifyServerUuid,
        string? CoolifyEnvironmentName,
        string? CoolifyGithubAppUuid,
        string? BuildPack,
        string? ComposeFileLocation = null,
        string? CustomDomain = null,
        string? DomainServiceName = null);
}
