# Patch: AuthController.cs (cross-origin refresh cookies)

## When to apply
- Gap indicates refresh cookies need `SameSite=None; Secure` for cross-origin auth.

## Instructions
1. Read the existing `AuthController.cs`.
2. If `SameSiteMode.None` is already used for refresh cookies in Production, make no changes.
3. Update cookie options on refresh/login responses to use:
   - `SameSite = SameSiteMode.None`
   - `Secure = true`
   - in Production environment only
4. Ensure route is `api/v1/auth` (not `api/Auth`).
5. Return the complete updated file.

## Greenfield stub (only if AuthController.cs does not exist)

```csharp
using Microsoft.AspNetCore.Mvc;

namespace DeployAI.Generated;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    [HttpPost("refresh")]
    public IActionResult Refresh() => Ok();
}
```
