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
    private readonly IServerDockerfileProvisioner _serverDockerfileProvisioner;

    public CoolifyManagementController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        CoolifyProvider coolifyProvider,
        IEncryptionService encryption,
        IServerDockerfileProvisioner serverDockerfileProvisioner)
    {
        _db = db;
        _currentUser = currentUser;
        _coolifyProvider = coolifyProvider;
        _encryption = encryption;
        _serverDockerfileProvisioner = serverDockerfileProvisioner;
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

        var dockerfilePath = request.DockerfilePath;
        var rootDirectory = request.RootDirectory;
        var buildPack = request.BuildPack;

        // Nixpacks can't build a .NET modular monolith (it runs `dotnet restore` where there is
        // no project, and ships an SDK older than net10.0). For a .NET server with no Dockerfile
        // yet, generate one and commit it, then create the app with the Dockerfile build pack.
        if (string.Equals(request.Framework, "dotnet", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(request.DockerfilePath) &&
            string.IsNullOrWhiteSpace(request.ComposeFileLocation))
        {
            var githubToken = await ResolveGitHubTokenAsync(cancellationToken);
            var repoParts = request.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries);
            if (githubToken is not null && repoParts.Length == 2)
            {
                var provisioned = await _serverDockerfileProvisioner.EnsureDockerfileAsync(
                    githubToken,
                    repoParts[0],
                    repoParts[1],
                    request.GitBranch,
                    request.RootDirectory ?? string.Empty,
                    request.ServiceDirectory,
                    cancellationToken);
                if (provisioned is not null)
                {
                    dockerfilePath = provisioned.DockerfileLocation;
                    rootDirectory = provisioned.BaseDirectory;
                    // Let ResolveBuildPack pick Dockerfile from the path; don't force nixpacks.
                    buildPack = null;
                }
            }
        }

        var project = await _coolifyProvider.CreateProjectAsync(
            new ProviderCredentials(token),
            new CreateProviderProjectRequest(
                request.Name,
                request.GitHubRepoFullName,
                request.Framework,
                rootDirectory,
                request.OutputDirectory,
                request.BuildCommand,
                request.InstallCommand,
                dockerfilePath,
                request.ServiceDirectory,
                request.StartCommand,
                request.GitBranch,
                request.IsPrivateRepository,
                request.CoolifyProjectUuid,
                request.CoolifyServerUuid,
                request.CoolifyEnvironmentName,
                request.CoolifyGithubAppUuid,
                buildPack,
                request.ComposeFileLocation,
                request.CustomDomain,
                request.DomainServiceName),
            cancellationToken);

        return Ok(new { project });
    }

    /// <summary>
    /// Attaches a compose app's domain to its web service. Separate from creation because a
    /// compose domain can only be set after the first deploy has parsed the compose file;
    /// callers deploy once, then call this, then redeploy.
    /// </summary>
    [HttpPost("coolify/applications/{applicationUuid}/compose-domain")]
    public async Task<IActionResult> AssignComposeDomain(
        string applicationUuid,
        [FromBody] AssignComposeDomainRequest request,
        CancellationToken cancellationToken)
    {
        var credential = await GetCoolifyCredentialAsync(request.CredentialId, cancellationToken);
        var token = _encryption.Decrypt(credential.TokenEncrypted);

        await _coolifyProvider.AssignComposeDomainAsync(
            new ProviderCredentials(token),
            applicationUuid,
            request.Domain,
            request.ServiceName,
            cancellationToken);

        return Ok(new { assigned = true });
    }

    /// <summary>
    /// Deletes a Coolify application that DeployAI no longer tracks — the orphans left
    /// behind when a wizard re-run created a fresh app and repointed the project at it.
    /// Refuses anything that is still a deploy target of one of the user's projects, so
    /// the live app can never be swept up with the strays.
    /// </summary>
    [HttpDelete("coolify/applications/{applicationUuid}")]
    public async Task<IActionResult> DeleteOrphanApplication(
        string applicationUuid,
        [FromQuery] Guid credentialId,
        CancellationToken cancellationToken)
    {
        var credential = await GetCoolifyCredentialAsync(credentialId, cancellationToken);

        var userId = _currentUser.UserId!.Value;
        var isTracked = await _db.DeployTargets
            .AnyAsync(
                t => t.Project.UserId == userId && t.ProviderProjectId == applicationUuid,
                cancellationToken);
        if (isTracked)
        {
            throw new DeployAIException(
                "target_in_use",
                "That application belongs to one of your apps. Remove the app in DeployAI instead of deleting it here.");
        }

        var token = _encryption.Decrypt(credential.TokenEncrypted);
        await _coolifyProvider.DeleteProjectAsync(
            new ProviderCredentials(token),
            applicationUuid,
            cancellationToken);

        return Ok(new { deleted = applicationUuid });
    }

    private async Task<string?> ResolveGitHubTokenAsync(CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId
            ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user?.GitHubTokenEncrypted is not { Length: > 0 })
        {
            return null;
        }

        return _encryption.Decrypt(user.GitHubTokenEncrypted);
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

    public sealed record AssignComposeDomainRequest(
        Guid CredentialId,
        string Domain,
        string? ServiceName);
}
