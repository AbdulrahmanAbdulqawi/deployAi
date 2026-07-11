using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public NotificationsController(DeployAIDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var prefs = await GetOrCreatePreferencesAsync(userId, cancellationToken);
        return Ok(MapPreferences(prefs));
    }

    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences(
        [FromBody] UpdateNotificationPreferencesRequest request,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var prefs = await GetOrCreatePreferencesAsync(userId, cancellationToken);
        prefs.EmailOnSuccess = request.EmailOnSuccess;
        prefs.EmailOnFailure = request.EmailOnFailure;
        prefs.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapPreferences(prefs));
    }

    private async Task<NotificationPreference> GetOrCreatePreferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var prefs = await _db.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (prefs is not null)
        {
            return prefs;
        }

        prefs = new NotificationPreference
        {
            UserId = userId,
            EmailOnSuccess = true,
            EmailOnFailure = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _db.NotificationPreferences.Add(prefs);
        await _db.SaveChangesAsync(cancellationToken);
        return prefs;
    }

    private static object MapPreferences(NotificationPreference prefs) => new
    {
        emailOnSuccess = prefs.EmailOnSuccess,
        emailOnFailure = prefs.EmailOnFailure,
        emailOnComplete = prefs.EmailOnSuccess
    };

    private Guid RequireUserId() =>
        _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");

    public sealed record UpdateNotificationPreferencesRequest(bool EmailOnSuccess, bool EmailOnFailure);
}
