using DeployAI.Core.Deployments;

namespace DeployAI.Data.Entities;

/// <summary>
/// Where one check stands right now, and how it got there.
/// </summary>
/// <remarks>
/// <para>
/// The current picture, kept beside the append-only run history rather than derived from it: the
/// fleet view asks "what is every check on every project doing" on every page load, and that should
/// be one indexed scan, not a window function over every run ever recorded.
/// </para>
/// <para>
/// It is also the notification ledger. <see cref="LastConclusiveStatus"/> and
/// <see cref="LastNotifiedStatus"/> are what keep an alert honest when DeployAI cannot see: an
/// inconclusive sweep must leave both untouched, or a blip during an outage reports a recovery that
/// never happened. The rules live in <see cref="CheckLedgerTransitions"/>, away from storage, because
/// they are the part that is easy to get silently wrong.
/// </para>
/// </remarks>
public class ProjectCheckState
{
    public Guid ProjectId { get; set; }

    /// <summary>Stable across runs — this plus the project is the identity of the row.</summary>
    public string CheckId { get; set; } = string.Empty;

    public Guid? DeployTargetId { get; set; }

    public string Target { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Url { get; set; }

    public string? SuggestedAction { get; set; }

    /// <summary>The most recent answer, including <see cref="VerificationCheckStatus.Inconclusive"/>.</summary>
    public VerificationCheckStatus Status { get; set; } = VerificationCheckStatus.Skipped;

    /// <summary>The last answer that was actually about the app. Never inconclusive, never skipped.</summary>
    public VerificationCheckStatus? LastConclusiveStatus { get; set; }

    public DateTimeOffset? LastConclusiveAt { get; set; }

    public DateTimeOffset FirstObservedAt { get; set; }

    /// <summary>
    /// Every sweep touches this, whatever the answer. It is how a check that stopped being produced
    /// is eventually recognised as stale rather than sitting at a frozen status forever.
    /// </summary>
    public DateTimeOffset LastObservedAt { get; set; }

    /// <summary>Moves only on a conclusive change, so "unchanged for 3 weeks" means what it says.</summary>
    public DateTimeOffset StatusChangedAt { get; set; }

    public int ConsecutiveFailures { get; set; }

    public int ConsecutiveInconclusive { get; set; }

    public VerificationCheckStatus? LastNotifiedStatus { get; set; }

    public DateTimeOffset? LastNotifiedAt { get; set; }

    public Project Project { get; set; } = null!;

    /// <summary>Reads the transition-relevant fields out, for the pure rules to advance.</summary>
    public CheckLedgerState ToLedger() => new(
        Status,
        LastConclusiveStatus,
        LastConclusiveAt,
        StatusChangedAt,
        ConsecutiveFailures,
        ConsecutiveInconclusive,
        LastNotifiedStatus,
        LastNotifiedAt);

    /// <summary>Writes an advanced ledger back. The only place these fields are assigned.</summary>
    public void ApplyLedger(CheckLedgerState ledger)
    {
        Status = ledger.Status;
        LastConclusiveStatus = ledger.LastConclusiveStatus;
        LastConclusiveAt = ledger.LastConclusiveAt;
        StatusChangedAt = ledger.StatusChangedAt;
        ConsecutiveFailures = ledger.ConsecutiveFailures;
        ConsecutiveInconclusive = ledger.ConsecutiveInconclusive;
        LastNotifiedStatus = ledger.LastNotifiedStatus;
        LastNotifiedAt = ledger.LastNotifiedAt;
    }
}
