using System.Text.Json.Serialization;
using DeployAI.Core.Providers;

namespace DeployAI.Core.Domains;

/// <summary>Whether a domain can be bought, and why not when it cannot.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DomainAvailability
{
    /// <summary>Could not be determined — never shown as unavailable.</summary>
    Unknown = 0,

    Available = 1,

    /// <summary>Someone already owns it.</summary>
    Taken = 2,

    /// <summary>The registry will not sell it at a normal price, or at all through this registrar.</summary>
    Unsupported = 3
}

/// <summary>
/// What a domain would cost, in the only units that avoid rounding arguments.
/// </summary>
/// <param name="FirstYearCents">
/// What is charged now. Must be handed back verbatim at purchase — registrars reject a mismatch,
/// which is what makes it impossible to be charged a figure that was never displayed.
/// </param>
/// <param name="RenewalCents">
/// What is charged every year after. Frequently several times the first year, and the number
/// people are surprised by, so it is never optional here.
/// </param>
/// <param name="IsFirstYearPromotional">Whether the two differ because of an introductory offer.</param>
/// <param name="IsPremium">
/// A registry-priced name, sometimes hundreds or thousands. Worth saying out loud rather than
/// letting it read as an ordinary price.
/// </param>
public sealed record DomainPrice(
    int FirstYearCents,
    int RenewalCents,
    bool IsFirstYearPromotional,
    bool IsPremium,
    int MinimumYears);

/// <summary>One domain a search turned up.</summary>
public sealed record DomainOffer(
    string Hostname,
    DomainAvailability Availability,
    DomainPrice? Price,
    string Message);

/// <summary>The outcome of a registration attempt.</summary>
/// <param name="ChargedCents">What was actually charged, read back from the registrar.</param>
public sealed record DomainRegistration(
    bool Succeeded,
    string Hostname,
    string? OrderId,
    int? ChargedCents,
    string Message);

/// <summary>
/// A registrar DeployAI can buy a domain through, using the user's own account.
/// </summary>
/// <remarks>
/// The user's own credentials throughout: they own the domain, they are charged directly, their
/// verified contact details are used, and there is no reseller relationship for DeployAI to be on
/// the wrong end of. That is what makes this a feature rather than a business.
/// </remarks>
public interface IDomainRegistrar
{
    string ProviderName { get; }

    string DisplayName { get; }

    /// <summary>
    /// Whether a domain can be bought and what it costs. Read-only and spends nothing, but is rate
    /// limited by the registrar — so this belongs behind a deliberate search, never a keystroke.
    /// </summary>
    Task<DomainOffer> CheckAvailabilityAsync(
        ProviderCredentials credentials, string hostname, CancellationToken cancellationToken);

    /// <summary>
    /// Runs every pre-flight the registrar offers — availability, price match, eligibility, funds
    /// — without buying anything.
    /// </summary>
    Task<DomainRegistration> DryRunAsync(
        ProviderCredentials credentials,
        string hostname,
        int expectedCostCents,
        CancellationToken cancellationToken);

    /// <summary>
    /// Buys the domain. Spends real money.
    /// </summary>
    /// <param name="expectedCostCents">
    /// The exact figure the user was shown. Registrars refuse a mismatch, so a price that moved
    /// between quote and purchase fails loudly instead of quietly charging the new one.
    /// </param>
    /// <param name="idempotencyKey">
    /// Makes a retry safe. Without it a timeout leaves nobody able to say whether a domain was
    /// bought, and asking again risks buying it twice.
    /// </param>
    Task<DomainRegistration> RegisterAsync(
        ProviderCredentials credentials,
        string hostname,
        int expectedCostCents,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public interface IDomainRegistrarFactory
{
    IDomainRegistrar? GetRegistrar(string providerName);

    IReadOnlyList<IDomainRegistrar> All { get; }
}
