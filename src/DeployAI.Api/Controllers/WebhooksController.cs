using DeployAI.Api.Services;
using DeployAI.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Controllers;

/// <summary>
/// Receives inbound webhook callbacks from external services. Unauthenticated by user token -
/// each handler validates its own payload signature instead.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks")]
public sealed class WebhooksController : ControllerBase
{
    private readonly IGitHubWebhookHandler _handler;
    private readonly GitHubOptions _gitHubOptions;

    public WebhooksController(
        IGitHubWebhookHandler handler,
        IOptions<GitHubOptions> gitHubOptions)
    {
        _handler = handler;
        _gitHubOptions = gitHubOptions.Value;
    }

    /// <summary>
    /// Handles a GitHub webhook delivery. Validates the <c>X-Hub-Signature-256</c> header against
    /// the configured webhook secret and, for <c>push</c> events only, triggers auto-deploy for any
    /// project tracking the pushed branch. Non-push events and deliveries received before a webhook
    /// secret is configured are acknowledged without action.
    /// </summary>
    [HttpPost("github")]
    public async Task<IActionResult> GitHub(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_gitHubOptions.WebhookSecret))
        {
            return NotFound();
        }

        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        var signature = Request.Headers["X-Hub-Signature-256"].ToString();
        var eventName = Request.Headers["X-GitHub-Event"].ToString();

        if (!string.Equals(eventName, "push", StringComparison.OrdinalIgnoreCase))
        {
            return Ok();
        }

        await _handler.HandlePushAsync(payload, signature, cancellationToken);
        return Ok();
    }
}
