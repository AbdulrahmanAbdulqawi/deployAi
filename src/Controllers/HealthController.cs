using Microsoft.AspNetCore.Mvc;

namespace DeployAI.Generated;

[ApiController]
[Route("api/v1/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "healthy" });
}