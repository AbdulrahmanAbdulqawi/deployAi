using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeployAI.Api.Controllers;

/// <summary>
/// The domains a project should be reachable at, and how far each has got towards serving HTTPS.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/domains")]
public sealed class DomainsController : ControllerBase
{
    private readonly IDomainService _domains;
    private readonly ICurrentUserService _currentUser;

    public DomainsController(IDomainService domains, ICurrentUserService currentUser)
    {
        _domains = domains;
        _currentUser = currentUser;
    }

    /// <summary>Lists the project's domains, the primary one first.</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken cancellationToken) =>
        Ok(await _domains.ListAsync(RequireUserId(), projectId, cancellationToken));

    /// <summary>
    /// Records a domain for one part of the project and starts checking it. Nothing is written to
    /// the provider until DNS is confirmed to reach the server.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Attach(
        Guid projectId,
        [FromBody] AttachDomainRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _domains.AttachAsync(
            RequireUserId(), projectId, request.DeployTargetId, request.Domain, cancellationToken));

    /// <summary>
    /// Starts the checks again from the beginning. The button a user needs after a domain ended up
    /// unverifiable, which is a state that says nothing was wrong — only that nothing could be seen.
    /// </summary>
    [HttpPost("{domainId:guid}/recheck")]
    public async Task<IActionResult> Recheck(
        Guid projectId, Guid domainId, CancellationToken cancellationToken)
    {
        _ = projectId;
        return Ok(await _domains.RetryAsync(RequireUserId(), domainId, cancellationToken));
    }

    /// <summary>
    /// What the project could be reached at without the user configuring anything: a name under
    /// DeployAI's own zone, plus any zones their connected DNS accounts can write to.
    /// </summary>
    [HttpGet("options")]
    public async Task<IActionResult> Options(Guid projectId, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        return Ok(new
        {
            suggestedSubdomain = await _domains.SuggestPlatformSubdomainAsync(userId, projectId, cancellationToken),
            zones = await _domains.ListConnectedZonesAsync(userId, cancellationToken)
        });
    }

    /// <summary>Removes a domain from the project.</summary>
    [HttpDelete("{domainId:guid}")]
    public async Task<IActionResult> Remove(
        Guid projectId, Guid domainId, CancellationToken cancellationToken)
    {
        _ = projectId;
        await _domains.RemoveAsync(RequireUserId(), domainId, cancellationToken);
        return NoContent();
    }

    private Guid RequireUserId() =>
        _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");

    public sealed record AttachDomainRequest(Guid DeployTargetId, string Domain);
}
