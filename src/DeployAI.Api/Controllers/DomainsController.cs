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
    private readonly IDomainPurchaseService _purchases;
    private readonly ICurrentUserService _currentUser;

    public DomainsController(
        IDomainService domains,
        IDomainPurchaseService purchases,
        ICurrentUserService currentUser)
    {
        _domains = domains;
        _purchases = purchases;
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

    /// <summary>
    /// Looks up whether a domain can be bought and what it would cost, recording the price so a
    /// purchase can only ever be made at the figure that was shown.
    /// </summary>
    /// <remarks>
    /// Spends nothing, but the registrar rate-limits availability lookups — so this belongs behind
    /// a deliberate search rather than firing on every keystroke.
    /// </remarks>
    [HttpPost("search")]
    public async Task<IActionResult> Search(
        Guid projectId,
        [FromBody] SearchDomainsRequest request,
        CancellationToken cancellationToken) =>
        Ok(new
        {
            results = await _purchases.SearchAsync(
                RequireUserId(), request.Name, projectId, request.DeployTargetId, cancellationToken)
        });

    /// <summary>
    /// Buys a domain. Spends real money.
    /// </summary>
    /// <remarks>
    /// Takes a quote id rather than a price, so nothing can ask to be charged a figure the user was
    /// never shown, and requires the registration agreement to be accepted explicitly rather than
    /// on their behalf. The quote id is also the idempotency key, so a retry after a timeout cannot
    /// buy the same domain twice.
    /// </remarks>
    [HttpPost("purchase")]
    public async Task<IActionResult> Purchase(
        Guid projectId,
        [FromBody] PurchaseDomainRequest request,
        CancellationToken cancellationToken)
    {
        _ = projectId;
        return Ok(await _purchases.PurchaseAsync(
            RequireUserId(), request.QuoteId, request.AgreeToTerms, cancellationToken));
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

    public sealed record SearchDomainsRequest(string Name, Guid? DeployTargetId = null);

    /// <param name="AgreeToTerms">
    /// The registrar requires this per purchase and refuses without it. DeployAI asks for it in its
    /// own right rather than sending "yes" on someone's behalf.
    /// </param>
    public sealed record PurchaseDomainRequest(Guid QuoteId, bool AgreeToTerms);
}
