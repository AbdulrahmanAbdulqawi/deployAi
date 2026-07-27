using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Railway;

namespace DeployAI.Api.Services;

/// <summary>
/// Resolves a usable, decrypted bearer token for a stored connection - for Railway OAuth
/// connections specifically, this also transparently refreshes an expired access token and
/// persists the new one, so callers never have to deal with token expiry themselves.
/// </summary>
public interface IProviderCredentialTokenService
{
    /// <summary>Decrypts a credential's token, refreshing it first if it's an expired Railway OAuth token.</summary>
    Task<string> GetTokenAsync(ProviderCredential credential, CancellationToken cancellationToken);
}

public sealed class ProviderCredentialTokenService : IProviderCredentialTokenService
{
    private readonly IEncryptionService _encryption;
    private readonly IRailwayOAuthService _railwayOAuth;
    private readonly DeployAIDbContext _db;

    public ProviderCredentialTokenService(
        IEncryptionService encryption,
        IRailwayOAuthService railwayOAuth,
        DeployAIDbContext db)
    {
        _encryption = encryption;
        _railwayOAuth = railwayOAuth;
        _db = db;
    }

    public async Task<string> GetTokenAsync(ProviderCredential credential, CancellationToken cancellationToken)
    {
        if (!string.Equals(credential.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
        {
            return _encryption.Decrypt(credential.TokenEncrypted);
        }

        var stored = _encryption.Decrypt(credential.TokenEncrypted);
        var payload = RailwayOAuthTokenStorage.TryParse(stored);
        if (payload is null)
        {
            return stored;
        }

        if (payload.ExpiresAtUtc > DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(payload.RefreshToken))
        {
            return payload.AccessToken;
        }

        var refreshed = await _railwayOAuth.RefreshTokenAsync(payload.RefreshToken, cancellationToken);
        credential.TokenEncrypted = _encryption.Encrypt(RailwayOAuthTokenStorage.Serialize(refreshed));
        await _db.SaveChangesAsync(cancellationToken);
        return refreshed.AccessToken;
    }
}
