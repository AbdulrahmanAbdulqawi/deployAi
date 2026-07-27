using DeployAI.Api.Services;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using DeployAI.Infrastructure.Railway;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Controllers;

/// <summary>
/// Handles the Railway OAuth "Connect Railway" flow: builds the authorization URL for a signed-in
/// user, then on callback exchanges the code and stores the resulting token as a provider
/// connection.
/// </summary>
[ApiController]
[Route("api/auth/railway")]
public sealed class RailwayAuthController : ControllerBase
{
    private readonly IRailwayOAuthService _railwayOAuth;
    private readonly DeployAIDbContext _db;
    private readonly IEncryptionService _encryption;
    private readonly IOAuthStateStore _stateStore;
    private readonly ICurrentUserService _currentUser;
    private readonly AppOptions _appOptions;

    public RailwayAuthController(
        IRailwayOAuthService railwayOAuth,
        DeployAIDbContext db,
        IEncryptionService encryption,
        IOAuthStateStore stateStore,
        ICurrentUserService currentUser,
        IOptions<AppOptions> appOptions)
    {
        _railwayOAuth = railwayOAuth;
        _db = db;
        _encryption = encryption;
        _stateStore = stateStore;
        _currentUser = currentUser;
        _appOptions = appOptions.Value;
    }

    /// <summary>
    /// Builds the Railway OAuth authorization URL for the current user, embedding their user id
    /// and an optional post-connect return path in the anti-CSRF state value.
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
        var url = _railwayOAuth.BuildAuthorizationUrl(state);
        return Ok(new { url });
    }

    /// <summary>
    /// Completes Railway OAuth: exchanges the code for a token, stores it as a provider connection
    /// for the user embedded in <paramref name="state"/> (creating or updating one with the same
    /// label as the Railway account name), then redirects back to the frontend. Redirects with an
    /// error/invalid_state query param instead of failing outright when the flow can't complete.
    /// </summary>
    /// <param name="code">The Railway OAuth authorization code.</param>
    /// <param name="state">The anti-CSRF state value issued by <see cref="GetLoginUrl"/>.</param>
    /// <param name="error">An error code from Railway, if the user declined/cancelled.</param>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return Redirect($"{_appOptions.FrontendUrl}/settings?railway=error");
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) ||
            !_stateStore.TryValidateAndConsume(state, out var payload) || payload?.UserId is null)
        {
            return Redirect($"{_appOptions.FrontendUrl}/settings?railway=invalid_state");
        }

        var tokens = await _railwayOAuth.ExchangeCodeForTokenAsync(code, cancellationToken);
        var profile = await _railwayOAuth.GetUserProfileAsync(tokens.AccessToken, cancellationToken);
        var label = string.IsNullOrWhiteSpace(profile.Name)
            ? "Railway"
            : profile.Name!;

        var existing = await _db.ProviderCredentials
            .FirstOrDefaultAsync(c =>
                c.UserId == payload.UserId.Value &&
                c.ProviderName == "railway" &&
                c.Label == label,
                cancellationToken);

        if (existing is null)
        {
            existing = new ProviderCredential
            {
                Id = Guid.NewGuid(),
                UserId = payload.UserId.Value,
                ProviderName = "railway",
                Label = label,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.ProviderCredentials.Add(existing);
        }

        existing.TokenEncrypted = _encryption.Encrypt(RailwayOAuthTokenStorage.Serialize(tokens));
        existing.IsValid = true;
        existing.LastValidatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        var destination = string.IsNullOrWhiteSpace(payload.ReturnUrl)
            ? "/settings"
            : payload.ReturnUrl;
        var separator = destination.Contains('?') ? '&' : '?';
        return Redirect($"{_appOptions.FrontendUrl}{destination}{separator}railway=connected");
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/'))
        {
            return "/settings";
        }

        return returnUrl;
    }
}
