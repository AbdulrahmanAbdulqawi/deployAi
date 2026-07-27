using DeployAI.Api.Services;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using DeployAI.Infrastructure.Vercel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Controllers;

/// <summary>
/// Handles the Vercel OAuth "Connect Vercel" flow: builds the authorization URL for a signed-in
/// user, then on callback exchanges the code and stores the resulting token as a provider
/// connection (single "Default"-labeled connection per user).
/// </summary>
[ApiController]
[Route("api/auth/vercel")]
public sealed class VercelAuthController : ControllerBase
{
    private readonly IVercelOAuthService _vercelOAuth;
    private readonly DeployAIDbContext _db;
    private readonly IEncryptionService _encryption;
    private readonly IOAuthStateStore _stateStore;
    private readonly ICurrentUserService _currentUser;
    private readonly AppOptions _appOptions;

    public VercelAuthController(
        IVercelOAuthService vercelOAuth,
        DeployAIDbContext db,
        IEncryptionService encryption,
        IOAuthStateStore stateStore,
        ICurrentUserService currentUser,
        IOptions<AppOptions> appOptions)
    {
        _vercelOAuth = vercelOAuth;
        _db = db;
        _encryption = encryption;
        _stateStore = stateStore;
        _currentUser = currentUser;
        _appOptions = appOptions.Value;
    }

    /// <summary>
    /// Builds the Vercel OAuth authorization URL for the current user as JSON, for callers that
    /// want to navigate to it themselves (e.g. opening in a popup) rather than being redirected.
    /// </summary>
    /// <param name="returnUrl">Frontend path to redirect to after connecting (must start with '/').</param>
    [Authorize]
    [HttpGet("login-url")]
    public IActionResult GetLoginUrl([FromQuery] string? returnUrl)
    {
        var userId = _currentUser.UserId;
        if (userId is null)
        {
            return Unauthorized();
        }

        var safeReturnUrl = NormalizeReturnUrl(returnUrl);
        var state = _stateStore.CreateState(new OAuthStatePayload(userId.Value, safeReturnUrl));
        var url = _vercelOAuth.BuildAuthorizationUrl(state);
        return Ok(new { url });
    }

    /// <summary>Same as <see cref="GetLoginUrl"/> but redirects directly instead of returning JSON.</summary>
    /// <param name="returnUrl">Frontend path to redirect to after connecting (must start with '/').</param>
    [Authorize]
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl)
    {
        var userId = _currentUser.UserId;
        if (userId is null)
        {
            return Unauthorized();
        }

        var safeReturnUrl = NormalizeReturnUrl(returnUrl);
        var state = _stateStore.CreateState(new OAuthStatePayload(userId.Value, safeReturnUrl));
        var url = _vercelOAuth.BuildAuthorizationUrl(state);
        return Redirect(url);
    }

    /// <summary>
    /// Completes Vercel OAuth: exchanges the code for a token, stores/updates the user's single
    /// "Default" Vercel connection, then redirects back to the frontend. Redirects with an
    /// error/invalid_state query param instead of failing outright when the flow can't complete.
    /// </summary>
    /// <param name="code">The Vercel OAuth authorization code.</param>
    /// <param name="state">The anti-CSRF state value issued by <see cref="GetLoginUrl"/>/<see cref="Login"/>.</param>
    /// <param name="error">An error code from Vercel, if the user declined/cancelled.</param>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return Redirect($"{_appOptions.FrontendUrl}/settings?vercel=error");
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) ||
            !_stateStore.TryValidateAndConsume(state, out var payload) || payload?.UserId is null)
        {
            return Redirect($"{_appOptions.FrontendUrl}/settings?vercel=invalid_state");
        }

        var accessToken = await _vercelOAuth.ExchangeCodeForTokenAsync(code, cancellationToken);
        await _vercelOAuth.GetUserProfileAsync(accessToken, cancellationToken);

        var existing = await _db.ProviderCredentials
            .FirstOrDefaultAsync(c =>
                c.UserId == payload.UserId.Value &&
                c.ProviderName == "vercel" &&
                c.Label == "Default",
                cancellationToken);

        if (existing is null)
        {
            existing = new ProviderCredential
            {
                Id = Guid.NewGuid(),
                UserId = payload.UserId.Value,
                ProviderName = "vercel",
                Label = "Default",
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.ProviderCredentials.Add(existing);
        }

        existing.TokenEncrypted = _encryption.Encrypt(accessToken);
        existing.IsValid = true;
        existing.LastValidatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        var destination = string.IsNullOrWhiteSpace(payload.ReturnUrl)
            ? "/settings"
            : payload.ReturnUrl;
        var separator = destination.Contains('?') ? '&' : '?';
        return Redirect($"{_appOptions.FrontendUrl}{destination}{separator}vercel=connected");
    }

    private string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/'))
        {
            return "/settings";
        }

        return returnUrl;
    }
}
