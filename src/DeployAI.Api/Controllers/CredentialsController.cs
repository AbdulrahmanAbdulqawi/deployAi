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
public sealed class CredentialsController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IProviderFactory _providerFactory;
    private readonly IEncryptionService _encryption;
    private readonly IProviderCredentialTokenService _tokens;

    public CredentialsController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IProviderFactory providerFactory,
        IEncryptionService encryption,
        IProviderCredentialTokenService tokens)
    {
        _db = db;
        _currentUser = currentUser;
        _providerFactory = providerFactory;
        _encryption = encryption;
        _tokens = tokens;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var credentials = await _db.ProviderCredentials
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.ProviderName)
            .Select(c => new
            {
                id = c.Id,
                providerName = c.ProviderName,
                label = c.Label,
                isValid = c.IsValid,
                lastValidatedAt = c.LastValidatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(new { credentials });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCredentialRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var provider = _providerFactory.GetProvider(request.ProviderName);
        var tokenToStore = BuildCredentialToken(provider.ProviderName, request);
        var isValid = await provider.ValidateCredentialsAsync(new ProviderCredentials(tokenToStore), cancellationToken);
        if (!isValid)
        {
            throw new DeployAIException("provider_token_invalid", $"Your {provider.DisplayName} connection needs refreshing. Check the access key and try again.");
        }

        var label = string.IsNullOrWhiteSpace(request.Label) ? "Default" : request.Label;
        if (string.Equals(provider.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
        {
            var existingRailway = await _db.ProviderCredentials
                .FirstOrDefaultAsync(
                    c => c.UserId == userId && c.ProviderName == "railway",
                    cancellationToken);
            if (existingRailway is not null)
            {
                existingRailway.Label = label;
                existingRailway.TokenEncrypted = _encryption.Encrypt(tokenToStore);
                existingRailway.IsValid = true;
                existingRailway.LastValidatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                return Ok(new
                {
                    id = existingRailway.Id,
                    providerName = existingRailway.ProviderName,
                    label = existingRailway.Label,
                    isValid = existingRailway.IsValid,
                    lastValidatedAt = existingRailway.LastValidatedAt
                });
            }
        }

        var credential = new ProviderCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProviderName = provider.ProviderName,
            Label = string.IsNullOrWhiteSpace(request.Label) ? "Default" : request.Label,
            TokenEncrypted = _encryption.Encrypt(tokenToStore),
            IsValid = true,
            LastValidatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.ProviderCredentials.Add(credential);
        await _db.SaveChangesAsync(cancellationToken);

        return Created($"/api/credentials/{credential.Id}", new
        {
            id = credential.Id,
            providerName = credential.ProviderName,
            label = credential.Label,
            isValid = credential.IsValid,
            lastValidatedAt = credential.LastValidatedAt
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var credential = await _db.ProviderCredentials.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);
        if (credential is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that connection." } });
        }

        var inUse = await _db.DeployTargets.AnyAsync(t => t.CredentialId == id, cancellationToken);
        if (inUse)
        {
            throw new DeployAIException("credential_in_use", "This connection is still used by one of your apps. Update the app first, then remove the connection.");
        }

        _db.ProviderCredentials.Remove(credential);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/projects")]
    public async Task<IActionResult> ListProviderProjects(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var credential = await _db.ProviderCredentials.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);
        if (credential is null)
        {
            return NotFound(new { error = new { code = "not_found", message = "We couldn't find that connection." } });
        }

        var provider = _providerFactory.GetProvider(credential.ProviderName);
        var token = await _tokens.GetTokenAsync(credential, cancellationToken);
        var projects = await provider.ListProjectsAsync(new ProviderCredentials(token), cancellationToken);
        return Ok(new { projects });
    }

    private static string BuildCredentialToken(string providerName, CreateCredentialRequest request)
    {
        if (!string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
        {
            return request.Token;
        }

        if (string.IsNullOrWhiteSpace(request.InstanceUrl))
        {
            throw new DeployAIException("coolify_instance_url_required", "Enter your Coolify instance URL (for example, https://coolify.example.com).");
        }

        return CoolifyCredentialStorage.Serialize(request.InstanceUrl, request.Token);
    }

    private Guid RequireUserId()
    {
        return _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
    }

    public sealed record CreateCredentialRequest(string ProviderName, string Token, string? Label, string? InstanceUrl);
}
