using System.Security.Claims;

namespace DeployAI.Api.Services;

/// <summary>Exposes the current authenticated request's user id, resolved from the JWT's "sub" claim.</summary>
public interface ICurrentUserService
{
    /// <summary>The signed-in user's id, or null if unauthenticated/the claim is missing.</summary>
    Guid? UserId { get; }
}

public sealed class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
            var sub = _httpContextAccessor.HttpContext?.User.FindFirstValue("sub");

            return Guid.TryParse(sub, out var userId) ? userId : null;
        }
    }
}
