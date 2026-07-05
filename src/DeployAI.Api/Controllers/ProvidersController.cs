using DeployAI.Core.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeployAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/providers")]
public sealed class ProvidersController : ControllerBase
{
    private readonly IProviderFactory _providerFactory;

    public ProvidersController(IProviderFactory providerFactory)
    {
        _providerFactory = providerFactory;
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult GetProviders()
    {
        return Ok(new
        {
            providers = _providerFactory.GetAvailableProviders()
        });
    }
}
