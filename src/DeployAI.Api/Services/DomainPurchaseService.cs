using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>A domain a search turned up, priced.</summary>
public sealed record DomainSearchResult(
    string Hostname,
    DomainAvailability Availability,
    string Message,
    Guid? QuoteId,
    int? FirstYearCents,
    int? RenewalCents,
    bool IsFirstYearPromotional,
    bool IsPremium,
    bool IsSandbox,
    DateTimeOffset? QuoteExpiresAt);

/// <summary>The outcome of trying to buy one.</summary>
public sealed record DomainPurchaseResult(
    bool Succeeded,
    string Hostname,
    string Message,
    int? ChargedCents,
    string? OrderId,
    Guid? DomainId);

public interface IDomainPurchaseService
{
    Task<IReadOnlyList<DomainSearchResult>> SearchAsync(
        Guid userId, string typedName, Guid? projectId, Guid? deployTargetId, CancellationToken cancellationToken);

    Task<DomainPurchaseResult> PurchaseAsync(
        Guid userId, Guid quoteId, bool agreeToTerms, CancellationToken cancellationToken);
}

/// <summary>
/// Searching for a domain and buying it, with the price the server's word rather than the caller's.
/// </summary>
/// <remarks>
/// Purchase takes a quote id and never an amount. A client cannot therefore ask to be charged
/// something the user was not shown, and the registrar independently refuses a cost that does not
/// match its own — so the guarantee survives even a bug in this file.
/// </remarks>
public sealed class DomainPurchaseService : IDomainPurchaseService
{
    /// <summary>
    /// Long enough to read a price and decide, short enough that it cannot be acted on after the
    /// registrar has moved it. A stale quote fails at the registrar anyway; this makes it fail
    /// here, where the message can be better.
    /// </summary>
    private static readonly TimeSpan QuoteLifetime = TimeSpan.FromMinutes(15);

    private readonly DeployAIDbContext _db;
    private readonly IDomainRegistrarFactory _registrars;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IDomainService _domains;
    private readonly TimeProvider _clock;
    private readonly ILogger<DomainPurchaseService> _logger;

    public DomainPurchaseService(
        DeployAIDbContext db,
        IDomainRegistrarFactory registrars,
        IProviderCredentialTokenService tokens,
        IDomainService domains,
        TimeProvider clock,
        ILogger<DomainPurchaseService> logger)
    {
        _db = db;
        _registrars = registrars;
        _tokens = tokens;
        _domains = domains;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DomainSearchResult>> SearchAsync(
        Guid userId,
        string typedName,
        Guid? projectId,
        Guid? deployTargetId,
        CancellationToken cancellationToken)
    {
        if (!DomainNameRules.TryNormalize(typedName, out var hostname, out var rejection))
        {
            throw new DeployAIException("domain_invalid", rejection!.Reason);
        }

        var (credential, registrar) = await RequireRegistrarAsync(userId, cancellationToken);
        var credentials = new ProviderCredentials(
            await _tokens.GetTokenAsync(credential, cancellationToken));

        var offer = await registrar.CheckAvailabilityAsync(credentials, hostname, cancellationToken);

        if (offer.Availability is not DomainAvailability.Available || offer.Price is null)
        {
            return
            [
                new DomainSearchResult(
                    hostname, offer.Availability, offer.Message,
                    null, null, null, false, false, false, null)
            ];
        }

        var sandbox = await PorkbunSandboxAsync(credential, cancellationToken);
        var now = _clock.GetUtcNow();

        // Written down at the moment it is shown, so the figure the user reads is the figure the
        // purchase can use, and nothing else.
        var quote = new DomainPurchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProjectId = projectId,
            DeployTargetId = deployTargetId,
            CredentialId = credential.Id,
            ProviderName = registrar.ProviderName,
            Hostname = hostname,
            Status = DomainPurchaseStatus.Quoted,
            FirstYearCents = offer.Price.FirstYearCents,
            RenewalCents = offer.Price.RenewalCents,
            IsFirstYearPromotional = offer.Price.IsFirstYearPromotional,
            IsPremium = offer.Price.IsPremium,
            IsSandbox = sandbox,
            ExpiresAt = now.Add(QuoteLifetime),
            StatusMessage = offer.Message,
            CreatedAt = now
        };

        _db.DomainPurchases.Add(quote);
        await _db.SaveChangesAsync(cancellationToken);

        return
        [
            new DomainSearchResult(
                hostname, offer.Availability, offer.Message, quote.Id,
                quote.FirstYearCents, quote.RenewalCents, quote.IsFirstYearPromotional,
                quote.IsPremium, sandbox, quote.ExpiresAt)
        ];
    }

    public async Task<DomainPurchaseResult> PurchaseAsync(
        Guid userId, Guid quoteId, bool agreeToTerms, CancellationToken cancellationToken)
    {
        // The registrar demands this per purchase and refuses without it. DeployAI asks for it in
        // its own right rather than sending "yes" on the user's behalf.
        if (!agreeToTerms)
        {
            throw new DeployAIException(
                "domain_terms_not_agreed",
                "Registering a domain means accepting the registrar's registration agreement. " +
                "Confirm to continue.");
        }

        var quote = await _db.DomainPurchases.FirstOrDefaultAsync(
            q => q.Id == quoteId && q.UserId == userId, cancellationToken)
            ?? throw new DeployAIException("domain_quote_not_found", "That price is no longer on offer.");

        if (quote.Status is not DomainPurchaseStatus.Quoted)
        {
            // Not an error worth alarming about: it usually means a double-submit, and the honest
            // answer is what already happened rather than a second attempt.
            return new DomainPurchaseResult(
                quote.Status is DomainPurchaseStatus.Completed,
                quote.Hostname,
                quote.Status is DomainPurchaseStatus.Completed
                    ? $"{quote.Hostname} was already bought."
                    : quote.StatusMessage,
                quote.ChargedCents,
                quote.OrderId,
                null);
        }

        var now = _clock.GetUtcNow();
        if (now > quote.ExpiresAt)
        {
            quote.Status = DomainPurchaseStatus.Expired;
            quote.StatusMessage = "That price expired before it was confirmed. Search again for a current one.";
            await _db.SaveChangesAsync(cancellationToken);
            throw new DeployAIException("domain_quote_expired", quote.StatusMessage);
        }

        var credential = await _db.ProviderCredentials.FirstOrDefaultAsync(
            c => c.Id == quote.CredentialId && c.UserId == userId, cancellationToken)
            ?? throw new DeployAIException(
                "dns_provider_unknown", "The account this was priced against is no longer connected.");

        var registrar = _registrars.GetRegistrar(quote.ProviderName)
            ?? throw new DeployAIException(
                "dns_provider_unknown", $"'{quote.ProviderName}' is no longer a registrar DeployAI supports.");

        var credentials = new ProviderCredentials(
            await _tokens.GetTokenAsync(credential, cancellationToken));

        // Re-priced immediately before buying. The registrar checks availability, eligibility,
        // funds and the cost match without charging, so a price that moved between the quote and
        // now fails here rather than becoming a surprise on a statement.
        var dryRun = await registrar.DryRunAsync(
            credentials, quote.Hostname, quote.FirstYearCents, cancellationToken);

        if (!dryRun.Succeeded)
        {
            quote.Status = DomainPurchaseStatus.Failed;
            quote.StatusMessage = dryRun.Message;
            await _db.SaveChangesAsync(cancellationToken);
            return new DomainPurchaseResult(false, quote.Hostname, dryRun.Message, null, null, null);
        }

        // The quote id doubles as the idempotency key, so a retry after a timeout returns the
        // original order rather than buying the domain twice.
        var registration = await registrar.RegisterAsync(
            credentials, quote.Hostname, quote.FirstYearCents, quote.Id.ToString(), cancellationToken);

        quote.Status = registration.Succeeded ? DomainPurchaseStatus.Completed : DomainPurchaseStatus.Failed;
        quote.StatusMessage = registration.Message;
        quote.ChargedCents = registration.ChargedCents;
        quote.OrderId = registration.OrderId;
        quote.CompletedAt = registration.Succeeded ? now : null;
        await _db.SaveChangesAsync(cancellationToken);

        if (!registration.Succeeded)
        {
            return new DomainPurchaseResult(false, quote.Hostname, registration.Message, null, null, null);
        }

        var domainId = await TryAttachAsync(userId, quote, cancellationToken);

        return new DomainPurchaseResult(
            true, quote.Hostname, registration.Message,
            registration.ChargedCents, registration.OrderId, domainId);
    }

    /// <summary>
    /// Hands the newly bought domain to the reconciler, so buying it is the last manual step.
    /// </summary>
    /// <remarks>
    /// Best-effort on purpose. The domain is bought and paid for whatever happens here, and a
    /// failure to wire it up must never read as a failed purchase — the user would try again and
    /// buy nothing, having already been charged.
    /// </remarks>
    private async Task<Guid?> TryAttachAsync(
        Guid userId, DomainPurchase quote, CancellationToken cancellationToken)
    {
        if (quote.ProjectId is not { } projectId || quote.DeployTargetId is not { } deployTargetId)
        {
            return null;
        }

        try
        {
            var view = await _domains.AttachAsync(
                userId, projectId, deployTargetId, quote.Hostname, cancellationToken);
            return view.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "Bought {Hostname} but could not attach it to the project automatically.", quote.Hostname);
            return null;
        }
    }

    private async Task<bool> PorkbunSandboxAsync(
        ProviderCredential credential, CancellationToken cancellationToken) =>
        // Read from the stored key rather than asked about, so a test order cannot later be
        // mistaken for a real one. Through the token service because the column holds ciphertext:
        // reading those bytes as text parses as nothing, which silently made every order real.
        credential.ProviderName.Equals("porkbun", StringComparison.OrdinalIgnoreCase) &&
        PorkbunCredentialStorage.IsSandbox(
            PorkbunCredentialStorage.TryParse(
                await _tokens.GetTokenAsync(credential, cancellationToken))?.ApiKey);

    private async Task<(ProviderCredential Credential, IDomainRegistrar Registrar)> RequireRegistrarAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var credentials = await _db.ProviderCredentials
            .Where(c => c.UserId == userId && c.Kind == CredentialKind.Dns)
            .OrderBy(c => c.Label)
            .ToListAsync(cancellationToken);

        foreach (var credential in credentials)
        {
            var registrar = _registrars.GetRegistrar(credential.ProviderName);
            if (registrar is not null)
            {
                return (credential, registrar);
            }
        }

        throw new DeployAIException("domain_registrar_not_connected", CannotBuyMessage(credentials.Count > 0));
    }

    /// <summary>
    /// Why buying is unavailable, and what to do about it.
    /// </summary>
    /// <remarks>
    /// Hosting DNS and selling domains are separate capabilities, and a user has no reason to know
    /// which of their accounts does which — so the message says which case they are in and names a
    /// provider that would work, rather than naming one they may not have connected. It previously
    /// blamed Cloudflare unconditionally, including when nothing at all was connected.
    /// </remarks>
    private string CannotBuyMessage(bool hasDnsOnlyAccount)
    {
        var suggestion = _registrars.All.Count > 0
            ? $"Connect {Humanise(_registrars.All.Select(r => r.DisplayName))} under Settings → Connections"
            : "No connected provider can register domains yet";

        return hasDnsOnlyAccount
            ? $"The account you've connected can point a domain you already own at this app, but it " +
              $"can't register a new one. {suggestion} to buy one here."
            : $"Buying a domain needs an account that can register names. {suggestion}, then search again.";
    }

    /// <summary>"Porkbun", "Porkbun or Namecheap", "A, B or C" — a list a person would read aloud.</summary>
    private static string Humanise(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            0 => string.Empty,
            1 => list[0],
            _ => $"{string.Join(", ", list.Take(list.Count - 1))} or {list[^1]}"
        };
    }
}
