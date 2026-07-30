using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Security;
using DeployAI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Controllers;

/// <summary>
/// Credentials saved once on the account and reused by every app that asks for them.
/// </summary>
[ApiController]
[Route("api/account")]
[Authorize]
public sealed class AccountController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly IEncryptionService _encryption;
    private readonly ICurrentUserService _currentUser;

    public AccountController(
        DeployAIDbContext db,
        IEncryptionService encryption,
        ICurrentUserService currentUser)
    {
        _db = db;
        _encryption = encryption;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Which credentials are saved — names only.
    /// </summary>
    /// <remarks>
    /// Never returns the values. A private key needs to reach a deploy form, and it does, through
    /// the env-schema response for the app that declares it; a settings page listing every stored
    /// secret in full has no such reason and would put them in a response nothing needed them in.
    /// </remarks>
    [HttpGet("secrets")]
    public async Task<IActionResult> GetSecrets(CancellationToken cancellationToken)
    {
        var store = await LoadAsync(cancellationToken);
        var set = store.NamesSet();

        return Ok(new
        {
            secrets = AccountSecretsStore.KnownNames.Select(name => new
            {
                name,
                isSet = set.Contains(name),
                // The private key is write-only once saved; the app id is not a secret and showing
                // it back is how a user checks they pasted the right one.
                value = name == AccountSecretsStore.GitHubAppId ? store.Find(name) : null,
                isSecret = name != AccountSecretsStore.GitHubAppId
            })
        });
    }

    /// <summary>
    /// Saves or clears the credentials on this account. An omitted field is left as it was; an
    /// explicitly empty one is cleared, which is how a user removes a key they no longer want stored.
    /// </summary>
    [HttpPut("secrets")]
    public async Task<IActionResult> SaveSecrets(
        [FromBody] SaveAccountSecretsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new DeployAIException("user_not_found", "We couldn't find your account.");

        var store = AccountSecretsStore.Parse(Decrypt(user.SavedSecretsEncrypted));

        if (request.GitHubAppId is not null)
        {
            store.Set(AccountSecretsStore.GitHubAppId, request.GitHubAppId);
        }

        if (request.GitHubAppPrivateKey is not null)
        {
            store.Set(AccountSecretsStore.GitHubAppPrivateKey, request.GitHubAppPrivateKey);
        }

        user.SavedSecretsEncrypted = _encryption.Encrypt(store.Serialize());
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return await GetSecrets(cancellationToken);
    }

    private async Task<AccountSecretsStore> LoadAsync(CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new DeployAIException("user_not_found", "We couldn't find your account.");

        return AccountSecretsStore.Parse(Decrypt(user.SavedSecretsEncrypted));
    }

    private string? Decrypt(byte[]? encrypted) =>
        encrypted is { Length: > 0 } ? _encryption.Decrypt(encrypted) : null;

    private Guid RequireUserId() =>
        _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");

    /// <param name="GitHubAppId">Null leaves it unchanged; empty clears it.</param>
    /// <param name="GitHubAppPrivateKey">Null leaves it unchanged; empty clears it.</param>
    public sealed record SaveAccountSecretsRequest(
        string? GitHubAppId = null,
        string? GitHubAppPrivateKey = null);
}
