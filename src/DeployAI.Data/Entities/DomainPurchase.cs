using System.Text.Json.Serialization;

namespace DeployAI.Data.Entities;

/// <summary>How far a purchase has got.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DomainPurchaseStatus
{
    /// <summary>Priced and shown to the user. Nothing has been spent.</summary>
    Quoted = 0,

    /// <summary>The registrar confirmed the order. Money has moved.</summary>
    Completed = 1,

    /// <summary>Attempted and refused. Nothing was spent.</summary>
    Failed = 2,

    /// <summary>The quote was never acted on before it lapsed.</summary>
    Expired = 3
}

/// <summary>
/// A quote to buy a domain, and afterwards the receipt for it.
/// </summary>
/// <remarks>
/// <para>
/// One row serves both because they are the same fact at two moments, and because a purchase needs
/// an audit trail that outlives the request that made it: what was quoted, when, what was actually
/// charged, and which order it became.
/// </para>
/// <para>
/// It also makes the price the server's word rather than the caller's. Purchasing accepts a quote
/// id, not an amount, so a client cannot ask to be charged something the user never saw — and the
/// registrar independently refuses a cost that does not match, which means the guarantee holds
/// even if this row were wrong.
/// </para>
/// </remarks>
public class DomainPurchase
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Set when the purchase was started for a specific project, so it can be wired up after.</summary>
    public Guid? ProjectId { get; set; }

    public Guid? DeployTargetId { get; set; }

    /// <summary>Which connected registrar account this was priced against, and will be bought through.</summary>
    public Guid CredentialId { get; set; }

    public string ProviderName { get; set; } = string.Empty;

    /// <summary>ASCII, lowercased, no scheme — the same normalisation every other hostname gets.</summary>
    public string Hostname { get; set; } = string.Empty;

    public DomainPurchaseStatus Status { get; set; } = DomainPurchaseStatus.Quoted;

    /// <summary>What was quoted for the first year, in cents. Restated to the registrar verbatim.</summary>
    public int FirstYearCents { get; set; }

    /// <summary>What it renews at every year after. Recorded because it is what people forget.</summary>
    public int RenewalCents { get; set; }

    public bool IsFirstYearPromotional { get; set; }

    public bool IsPremium { get; set; }

    /// <summary>
    /// Whether this was priced against a sandbox account, where no money is real. Stored rather
    /// than inferred so a test order can never be mistaken for a real one in hindsight.
    /// </summary>
    public bool IsSandbox { get; set; }

    /// <summary>
    /// After this, the quote cannot be acted on. Prices move, and charging yesterday's figure is
    /// exactly what the registrar's own cost check exists to prevent.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>What the registrar actually charged. Null until it has.</summary>
    public int? ChargedCents { get; set; }

    public string? OrderId { get; set; }

    /// <summary>Why it failed, or what happened. Never an exception's text.</summary>
    public string StatusMessage { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public User User { get; set; } = null!;
}
