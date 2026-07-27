using DeployAI.Core.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeployAI.Api.Controllers;

/// <summary>
/// Exposes the list of hosting providers (Vercel, Railway, Coolify) DeployAI can deploy to.
/// </summary>
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

    /// <summary>
    /// Lists every hosting provider registered with the app, regardless of whether the current
    /// user has connected a credential for it.
    /// </summary>
    /// <returns>The available providers (name, display name, API style).</returns>
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
