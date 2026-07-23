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
/// Object-storage connections and bucket browsing.
///
/// Deliberately separate from <see cref="CredentialsController"/>: that one resolves through
/// <see cref="IProviderFactory"/>, which only knows deployment providers and throws for anything
/// else. Storage credentials are persisted in the same table but tagged
/// <see cref="CredentialKind.ObjectStorage"/> so they stay out of deploy-target pickers.
/// </summary>
[ApiController]
[Authorize]
[Route("api/storage")]
public sealed class StorageController : ControllerBase
{
    private static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromMinutes(10);

    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IObjectStorageProviderFactory _storageFactory;
    private readonly IEncryptionService _encryption;

    public StorageController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IObjectStorageProviderFactory storageFactory,
        IEncryptionService encryption)
    {
        _db = db;
        _currentUser = currentUser;
        _storageFactory = storageFactory;
        _encryption = encryption;
    }

    [HttpGet("providers")]
    public IActionResult ListProviders() =>
        Ok(new { providers = _storageFactory.GetAvailableProviders() });

    [HttpGet("connections")]
    public async Task<IActionResult> ListConnections(CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var stored = await _db.ProviderCredentials
            .Where(c => c.UserId == userId && c.Kind == CredentialKind.ObjectStorage)
            .OrderBy(c => c.Label)
            .ToListAsync(cancellationToken);

        var connections = stored.Select(ToSummary).ToList();
        return Ok(new { connections });
    }

    [HttpPost("connections")]
    public async Task<IActionResult> CreateConnection(
        [FromBody] CreateStorageConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var providerName = string.IsNullOrWhiteSpace(request.ProviderName)
            ? ObjectStorageProviderNames.Hetzner
            : request.ProviderName;

        var provider = _storageFactory.GetObjectStorage(providerName)
            ?? throw new DeployAIException(
                "storage_provider_unknown",
                $"'{providerName}' is not a supported storage provider.");

        string tokenToStore;
        try
        {
            tokenToStore = StorageCredentialStorage.Serialize(
                request.Endpoint,
                request.Region ?? string.Empty,
                request.AccessKey,
                request.SecretKey);
        }
        catch (ArgumentException ex)
        {
            throw new DeployAIException("storage_connection_invalid", ex.Message);
        }

        // Validate before persisting anything, mirroring CredentialsController.Create.
        var isValid = await provider.ValidateCredentialsAsync(
            new ProviderCredentials(tokenToStore), cancellationToken);
        if (!isValid)
        {
            throw new DeployAIException(
                "storage_credentials_invalid",
                $"Couldn't reach {provider.DisplayName} with those keys. Check the endpoint, access key and secret key.");
        }

        var label = string.IsNullOrWhiteSpace(request.Label) ? "Default" : request.Label.Trim();

        var existing = await _db.ProviderCredentials.FirstOrDefaultAsync(
            c => c.UserId == userId
                 && c.ProviderName == providerName
                 && c.Label == label
                 && c.Kind == CredentialKind.ObjectStorage,
            cancellationToken);

        if (existing is not null)
        {
            existing.TokenEncrypted = _encryption.Encrypt(tokenToStore);
            existing.IsValid = true;
            existing.LastValidatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(ToSummary(existing));
        }

        var credential = new ProviderCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProviderName = providerName,
            Kind = CredentialKind.ObjectStorage,
            Label = label,
            TokenEncrypted = _encryption.Encrypt(tokenToStore),
            IsValid = true,
            LastValidatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.ProviderCredentials.Add(credential);
        await _db.SaveChangesAsync(cancellationToken);

        return Created($"/api/storage/connections/{credential.Id}", ToSummary(credential));
    }

    [HttpDelete("connections/{id:guid}")]
    public async Task<IActionResult> DeleteConnection(Guid id, CancellationToken cancellationToken)
    {
        var credential = await FindConnectionAsync(id, cancellationToken);
        _db.ProviderCredentials.Remove(credential);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("connections/{id:guid}/buckets")]
    public async Task<IActionResult> ListBuckets(Guid id, CancellationToken cancellationToken)
    {
        var (provider, credentials) = await ResolveAsync(id, cancellationToken);
        var buckets = await provider.ListBucketsAsync(credentials, cancellationToken);
        return Ok(new { buckets });
    }

    [HttpGet("connections/{id:guid}/buckets/{bucket}/objects")]
    public async Task<IActionResult> ListObjects(
        Guid id,
        string bucket,
        [FromQuery] string? prefix,
        [FromQuery] string? continuationToken,
        [FromQuery] int? maxKeys,
        CancellationToken cancellationToken)
    {
        var (provider, credentials) = await ResolveAsync(id, cancellationToken);
        var page = await provider.ListObjectsAsync(
            credentials,
            bucket,
            prefix,
            continuationToken,
            maxKeys ?? ObjectStorageLimits.DefaultListPageSize,
            cancellationToken);

        return Ok(page);
    }

    [HttpPost("connections/{id:guid}/buckets/{bucket}/delete")]
    public async Task<IActionResult> DeleteObjects(
        Guid id,
        string bucket,
        [FromBody] DeleteObjectsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Keys is null || request.Keys.Count == 0)
        {
            throw new DeployAIException("storage_no_keys", "Select at least one object to delete.");
        }

        if (request.Keys.Count > ObjectStorageLimits.MaxDeleteBatchSize)
        {
            throw new DeployAIException(
                "storage_delete_batch_too_large",
                $"Delete up to {ObjectStorageLimits.MaxDeleteBatchSize} objects at a time.");
        }

        var (provider, credentials) = await ResolveAsync(id, cancellationToken);
        var result = await provider.DeleteObjectsAsync(credentials, bucket, request.Keys, cancellationToken);
        return Ok(result);
    }

    [HttpGet("connections/{id:guid}/buckets/{bucket}/download")]
    public async Task<IActionResult> GetDownloadUrl(
        Guid id,
        string bucket,
        [FromQuery] string key,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new DeployAIException("storage_key_required", "Which object should be downloaded?");
        }

        var (provider, credentials) = await ResolveAsync(id, cancellationToken);
        var url = await provider.GetDownloadUrlAsync(
            credentials, bucket, key, DownloadUrlLifetime, cancellationToken);

        return Ok(new { url, expiresInSeconds = (int)DownloadUrlLifetime.TotalSeconds });
    }

    private async Task<(IObjectStorageProvider Provider, ProviderCredentials Credentials)> ResolveAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var credential = await FindConnectionAsync(id, cancellationToken);

        var provider = _storageFactory.GetObjectStorage(credential.ProviderName)
            ?? throw new DeployAIException(
                "storage_provider_unknown",
                $"'{credential.ProviderName}' is not a supported storage provider.");

        var token = _encryption.Decrypt(credential.TokenEncrypted);
        return (provider, new ProviderCredentials(token));
    }

    private async Task<ProviderCredential> FindConnectionAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        return await _db.ProviderCredentials.FirstOrDefaultAsync(
                   c => c.Id == id && c.UserId == userId && c.Kind == CredentialKind.ObjectStorage,
                   cancellationToken)
               ?? throw new DeployAIException("not_found", "We couldn't find that storage connection.");
    }

    /// <summary>Never returns the access key or secret key.</summary>
    private object ToSummary(ProviderCredential credential)
    {
        string? endpoint = null;
        string? region = null;
        try
        {
            var payload = StorageCredentialStorage.TryParse(_encryption.Decrypt(credential.TokenEncrypted));
            endpoint = payload?.Endpoint;
            region = payload?.Region;
        }
        catch
        {
            // A connection whose payload can't be read still lists, just without its details.
        }

        return new
        {
            id = credential.Id,
            providerName = credential.ProviderName,
            label = credential.Label,
            isValid = credential.IsValid,
            lastValidatedAt = credential.LastValidatedAt,
            endpoint,
            region
        };
    }

    private Guid RequireUserId() =>
        _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");

    public sealed record CreateStorageConnectionRequest(
        string? ProviderName,
        string Endpoint,
        string? Region,
        string AccessKey,
        string SecretKey,
        string? Label);

    public sealed record DeleteObjectsRequest(IReadOnlyList<string> Keys);
}
