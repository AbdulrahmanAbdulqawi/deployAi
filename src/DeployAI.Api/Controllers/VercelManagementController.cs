using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/credentials")]
public sealed class VercelManagementController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IEncryptionService _encryption;
    private readonly IProviderCredentialTokenService _tokens;

    public VercelManagementController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IProviderManagementFactory managementFactory,
        IEncryptionService encryption,
        IProviderCredentialTokenService tokens)
    {
        _db = db;
        _currentUser = currentUser;
        _managementFactory = managementFactory;
        _encryption = encryption;
        _tokens = tokens;
    }

    [HttpPost("vercel/projects")]
    public async Task<IActionResult> CreateVercelProject(
        [FromBody] CreateVercelProjectRequest request,
        CancellationToken cancellationToken)
    {
        var credential = await GetVercelCredentialAsync(request.CredentialId, cancellationToken);
        var management = _managementFactory.GetManagement("vercel");
        var token = _encryption.Decrypt(credential.TokenEncrypted);

        var project = await management.CreateProjectAsync(
            new ProviderCredentials(token),
            new CreateProviderProjectRequest(
                request.Name,
                request.GitHubRepoFullName,
                request.Framework,
                request.RootDirectory,
                request.OutputDirectory,
                request.BuildCommand,
                request.InstallCommand),
            cancellationToken);

        return Ok(new { project });
    }

    [HttpGet("{credentialId:guid}/projects/{projectId}/env")]
    public async Task<IActionResult> ListEnvVars(
        Guid credentialId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var credential = await GetCredentialAsync(credentialId, cancellationToken);
        var management = _managementFactory.GetManagement(credential.ProviderName);
        var token = await _tokens.GetTokenAsync(credential, cancellationToken);
        var envVars = await management.ListEnvVarsAsync(new ProviderCredentials(token), projectId, cancellationToken);
        return Ok(new { envVars });
    }

    [HttpPost("{credentialId:guid}/projects/{projectId}/env")]
    public async Task<IActionResult> UpsertEnvVar(
        Guid credentialId,
        string projectId,
        [FromBody] UpsertEnvVarRequest request,
        CancellationToken cancellationToken)
    {
        var credential = await GetCredentialAsync(credentialId, cancellationToken);
        var management = _managementFactory.GetManagement(credential.ProviderName);
        var token = await _tokens.GetTokenAsync(credential, cancellationToken);
        var envVar = await management.UpsertEnvVarAsync(
            new ProviderCredentials(token),
            projectId,
            new UpsertProviderEnvVarRequest(
                request.Key,
                request.Value,
                string.IsNullOrWhiteSpace(request.Type) ? "encrypted" : request.Type,
                request.Targets ?? ["production", "preview", "development"]),
            cancellationToken);

        return Ok(new { envVar });
    }

    [HttpDelete("{credentialId:guid}/projects/{projectId}/env/{envVarId}")]
    public async Task<IActionResult> DeleteEnvVar(
        Guid credentialId,
        string projectId,
        string envVarId,
        CancellationToken cancellationToken)
    {
        var credential = await GetCredentialAsync(credentialId, cancellationToken);
        var management = _managementFactory.GetManagement(credential.ProviderName);
        var token = await _tokens.GetTokenAsync(credential, cancellationToken);
        await management.DeleteEnvVarAsync(new ProviderCredentials(token), projectId, envVarId, cancellationToken);
        return NoContent();
    }

    private async Task<ProviderCredential> GetVercelCredentialAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await GetCredentialAsync(credentialId, cancellationToken);
        if (!string.Equals(credential.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeployAIException("invalid_credential", "This connection is not a Vercel connection.");
        }

        return credential;
    }

    private async Task<ProviderCredential> GetCredentialAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
        var credential = await _db.ProviderCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId, cancellationToken);

        return credential ?? throw new DeployAIException("not_found", "We couldn't find that connection.");
    }

    public sealed record CreateVercelProjectRequest(
        Guid CredentialId,
        string Name,
        string GitHubRepoFullName,
        string? Framework,
        string? RootDirectory,
        string? OutputDirectory,
        string? BuildCommand,
        string? InstallCommand);

    public sealed record UpsertEnvVarRequest(
        string Key,
        string Value,
        string? Type,
        IReadOnlyList<string>? Targets);
}
