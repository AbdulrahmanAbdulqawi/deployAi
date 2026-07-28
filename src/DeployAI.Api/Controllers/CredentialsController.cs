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

/// <summary>
/// Manages the current user's stored provider connections (Vercel/Railway/Coolify API
/// tokens/instance URLs), used as the credential a project's deploy targets authenticate with.
/// </summary>
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

    /// <summary>Lists the current user's stored provider connections.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        // Deployment credentials only — this list feeds deploy-target pickers, so
        // object-storage connections must not appear here (see StorageController).
        var stored = await _db.ProviderCredentials
            .Where(c => c.UserId == userId && c.Kind == CredentialKind.Deployment)
            .OrderBy(c => c.ProviderName)
            .ToListAsync(cancellationToken);

        var credentials = stored.Select(c => new
        {
            id = c.Id,
            providerName = c.ProviderName,
            label = c.Label,
            isValid = c.IsValid,
            lastValidatedAt = c.LastValidatedAt,
            instanceUrl = TryGetCoolifyInstanceUrl(c)
        }).ToList();

        return Ok(new { credentials });
    }

    /// <summary>
    /// Validates and stores a new provider connection (or, for Railway, updates the user's single
    /// existing Railway connection; for Coolify, updates an existing connection with the same
    /// label instead of creating a duplicate). Fails with <c>provider_token_invalid</c> if the
    /// provider rejects the token.
    /// </summary>
    /// <param name="request">Provider name, token, optional label, and (Coolify only) instance URL.</param>
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

        if (string.Equals(provider.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
        {
            var existingCoolify = await _db.ProviderCredentials
                .FirstOrDefaultAsync(
                    c => c.UserId == userId &&
                         c.ProviderName == ProviderNameValues.Coolify &&
                         c.Label == label,
                    cancellationToken);
            if (existingCoolify is not null)
            {
                existingCoolify.TokenEncrypted = _encryption.Encrypt(tokenToStore);
                existingCoolify.IsValid = true;
                existingCoolify.LastValidatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                return Ok(new
                {
                    id = existingCoolify.Id,
                    providerName = existingCoolify.ProviderName,
                    label = existingCoolify.Label,
                    isValid = existingCoolify.IsValid,
                    lastValidatedAt = existingCoolify.LastValidatedAt,
                    instanceUrl = TryGetCoolifyInstanceUrl(existingCoolify)
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
            lastValidatedAt = credential.LastValidatedAt,
            instanceUrl = TryGetCoolifyInstanceUrl(credential)
        });
    }

    /// <summary>
    /// Deletes a stored connection. Fails with <c>credential_in_use</c> if any deploy target still
    /// references it - the target must be updated to a different connection first.
    /// </summary>
    /// <param name="id">The connection to delete.</param>
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

    /// <summary>Lists the provider-side projects/applications visible to a stored connection.</summary>
    /// <param name="id">The connection to query through.</param>
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

    private string? TryGetCoolifyInstanceUrl(ProviderCredential credential)
    {
        if (!string.Equals(credential.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var decrypted = _encryption.Decrypt(credential.TokenEncrypted);
            return CoolifyCredentialStorage.TryParse(decrypted)?.InstanceUrl;
        }
        catch
        {
            return null;
        }
    }

    private Guid RequireUserId()
    {
        return _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
    }

    public sealed record CreateCredentialRequest(string ProviderName, string Token, string? Label, string? InstanceUrl);
}
