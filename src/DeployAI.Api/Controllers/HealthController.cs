using Microsoft.AspNetCore.Mvc;

namespace DeployAI.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class HealthController : ControllerBase
{
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "ok", service = "DeployAI" });
    }
}
