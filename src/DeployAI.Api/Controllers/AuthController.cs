using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Auth;
using DeployAI.Infrastructure.GitHub;
using DeployAI.Infrastructure.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Controllers;

/// <summary>
/// Handles GitHub OAuth sign-in and DeployAI's own session token lifecycle (access/refresh token
/// issuance, rotation, and revocation). Not protected by <c>[Authorize]</c> - each action either
/// starts a login flow or authenticates via a token/state value in the request itself.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IGitHubService _gitHubService;
    private readonly DeployAIDbContext _db;
    private readonly IEncryptionService _encryption;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IOAuthStateStore _stateStore;
    private readonly AppOptions _appOptions;

    public AuthController(
        IGitHubService gitHubService,
        DeployAIDbContext db,
        IEncryptionService encryption,
        IJwtTokenService jwtTokenService,
        IOAuthStateStore stateStore,
        IOptions<AppOptions> appOptions)
    {
        _gitHubService = gitHubService;
        _db = db;
        _encryption = encryption;
        _jwtTokenService = jwtTokenService;
        _stateStore = stateStore;
        _appOptions = appOptions.Value;
    }

    /// <summary>Starts GitHub OAuth login by redirecting to GitHub's authorization page.</summary>
    [HttpGet("github/login")]
    public IActionResult GitHubLogin()
    {
        var state = _stateStore.CreateState();
        var url = _gitHubService.BuildAuthorizationUrl(state);
        return Redirect(url);
    }

    /// <summary>
    /// Completes GitHub OAuth: exchanges the code for a token, creates or updates the local user
    /// record, issues a DeployAI access/refresh token pair, then redirects to the frontend with the
    /// tokens in the query string. Redirects with <c>?error=invalid_state</c> on a bad/expired
    /// state value instead of failing outright.
    /// </summary>
    /// <param name="code">The GitHub OAuth authorization code.</param>
    /// <param name="state">The anti-CSRF state value issued by <see cref="GitHubLogin"/>.</param>
    [HttpGet("github/callback")]
    public async Task<IActionResult> GitHubCallback([FromQuery] string code, [FromQuery] string state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || !_stateStore.ValidateAndConsume(state))
        {
            return Redirect($"{_appOptions.FrontendUrl}/auth/callback?error=invalid_state");
        }

        var accessToken = await _gitHubService.ExchangeCodeForTokenAsync(code, cancellationToken);
        var profile = await _gitHubService.GetUserProfileAsync(accessToken, cancellationToken);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.GitHubId == profile.Id, cancellationToken);
        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                GitHubId = profile.Id,
                GitHubLogin = profile.Login,
                Email = profile.Email,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Users.Add(user);
        }

        user.GitHubLogin = profile.Login;
        user.Email = profile.Email;
        user.GitHubTokenEncrypted = _encryption.Encrypt(accessToken);
        user.UpdatedAt = DateTimeOffset.UtcNow;

        var tokenPair = _jwtTokenService.CreateTokenPair(user.Id, user.GitHubLogin);
        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = _jwtTokenService.HashRefreshToken(tokenPair.RefreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);

        var redirect = $"{_appOptions.FrontendUrl}/auth/callback" +
                       $"?accessToken={Uri.EscapeDataString(tokenPair.AccessToken)}" +
                       $"&refreshToken={Uri.EscapeDataString(tokenPair.RefreshToken)}" +
                       $"&expiresIn={tokenPair.ExpiresIn}";
        return Redirect(redirect);
    }

    /// <summary>
    /// Exchanges a valid, unexpired refresh token for a new access/refresh token pair. The old
    /// refresh token is deleted atomically as part of the lookup so two concurrent refreshes with
    /// the same token can't both succeed.
    /// </summary>
    /// <param name="request">The refresh token to redeem.</param>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unauthorized(new { error = new { code = "invalid_refresh", message = "Your session expired. Sign in again." } });
        }

        var hash = _jwtTokenService.HashRefreshToken(request.RefreshToken);
        var stored = await _db.RefreshTokens
            .AsNoTracking()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.ExpiresAt > DateTimeOffset.UtcNow, cancellationToken);

        if (stored is null)
        {
            return Unauthorized(new { error = new { code = "invalid_refresh", message = "Your session expired. Sign in again." } });
        }

        // Atomic delete avoids DbUpdateConcurrencyException when two requests refresh with the same token.
        var deleted = await _db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.ExpiresAt > DateTimeOffset.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted == 0)
        {
            return Unauthorized(new { error = new { code = "invalid_refresh", message = "Your session expired. Sign in again." } });
        }

        var tokenPair = _jwtTokenService.CreateTokenPair(stored.UserId, stored.User.GitHubLogin);
        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = stored.UserId,
            TokenHash = _jwtTokenService.HashRefreshToken(tokenPair.RefreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            accessToken = tokenPair.AccessToken,
            refreshToken = tokenPair.RefreshToken,
            expiresIn = tokenPair.ExpiresIn
        });
    }

    /// <summary>Revokes the given refresh token, ending that session. A no-op if it's already gone.</summary>
    /// <param name="request">The refresh token to revoke.</param>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            var hash = _jwtTokenService.HashRefreshToken(request.RefreshToken);
            var tokens = await _db.RefreshTokens.Where(t => t.TokenHash == hash).ToListAsync(cancellationToken);
            _db.RefreshTokens.RemoveRange(tokens);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    public sealed record RefreshRequest(string RefreshToken);
}
