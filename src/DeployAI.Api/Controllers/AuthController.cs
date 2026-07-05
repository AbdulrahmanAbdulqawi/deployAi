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

    [HttpGet("github/login")]
    public IActionResult GitHubLogin()
    {
        var state = _stateStore.CreateState();
        var url = _gitHubService.BuildAuthorizationUrl(state);
        return Redirect(url);
    }

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

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unauthorized(new { error = new { code = "invalid_refresh", message = "Your session expired. Sign in again." } });
        }

        var hash = _jwtTokenService.HashRefreshToken(request.RefreshToken);
        var stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.ExpiresAt > DateTimeOffset.UtcNow, cancellationToken);

        if (stored is null)
        {
            return Unauthorized(new { error = new { code = "invalid_refresh", message = "Your session expired. Sign in again." } });
        }

        _db.RefreshTokens.Remove(stored);
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
