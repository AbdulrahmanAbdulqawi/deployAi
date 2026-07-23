using Microsoft.AspNetCore.Mvc;

namespace {{serverNamespace}};

// Reachable at https://<your-domain>/api/health through the nginx proxy, which is how
// post-deploy verification confirms the api service came up behind the web service.
[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "healthy" });
}
