namespace DeployAI.Core.Deployments;

/// <summary>The mutable history of one check, as it stands before or after an observation.</summary>
/// <param name="LastConclusiveStatus">
/// The last time this check actually concluded something. Never <see cref="VerificationCheckStatus.Inconclusive"/>
/// or <see cref="VerificationCheckStatus.Skipped"/>. Direct descendant of <c>ProjectDomain.LastConclusiveStatus</c>.
/// </param>
/// <param name="StatusChangedAt">When the conclusive answer last changed. Does not move for an inconclusive run.</param>
/// <param name="LastNotifiedStatus">What the user was last told. The edge, so "still failing" stays silent.</param>
public sealed record CheckLedgerState(
    VerificationCheckStatus Status,
    VerificationCheckStatus? LastConclusiveStatus,
    DateTimeOffset? LastConclusiveAt,
    DateTimeOffset StatusChangedAt,
    int ConsecutiveFailures,
    int ConsecutiveInconclusive,
    VerificationCheckStatus? LastNotifiedStatus,
    DateTimeOffset? LastNotifiedAt);

/// <summary>What, if anything, this observation is worth telling the user.</summary>
public enum CheckNotification
{
    /// <summary>Nothing changed that a person needs to hear about.</summary>
    None = 0,

    /// <summary>A check that was not failing now is.</summary>
    StartedFailing = 1,

    /// <summary>A check the user was told about is conclusive again and no longer failing.</summary>
    Recovered = 2,

    /// <summary>
    /// The check has been unable to run for long enough that the silence is itself the news. A
    /// monitor that quietly goes blind is the failure the whole inconclusive distinction exists to
    /// prevent, so it gets its own notification rather than none.
    /// </summary>
    WentBlind = 3
}

public sealed record CheckLedgerTransition(CheckLedgerState State, CheckNotification Notification);

/// <summary>
/// How one check's history advances when a new answer arrives.
/// </summary>
/// <remarks>
/// Pure and separate from storage because the rules are the part worth testing, and because getting
/// them wrong is silent: the dangerous mistake is letting an inconclusive observation reset the
/// ledger, which would send "recovered" the first time the network blipped during a real outage, and
/// send a duplicate "started failing" when it cleared.
/// </remarks>
public static class CheckLedgerTransitions
{
    public static CheckLedgerTransition Apply(
        CheckLedgerState? previous,
        VerificationCheckStatus observed,
        DateTimeOffset observedAt,
        int inconclusiveRunsBeforeNotify)
    {
        if (observed == VerificationCheckStatus.Inconclusive)
        {
            return Blind(previous, observedAt, inconclusiveRunsBeforeNotify);
        }

        if (observed == VerificationCheckStatus.Skipped)
        {
            return NoLongerApplies(previous, observedAt);
        }

        return Concluded(previous, observed, observedAt);
    }

    /// <summary>A real answer: the only kind that may move the ledger or notify.</summary>
    private static CheckLedgerTransition Concluded(
        CheckLedgerState? previous,
        VerificationCheckStatus observed,
        DateTimeOffset observedAt)
    {
        // The comparison is against the last *conclusive* answer, not the last answer. A check that
        // failed, went unreadable for an hour, and is failing again has not changed.
        var changed = previous?.LastConclusiveStatus != observed;

        var notification = CheckNotification.None;
        if (observed == VerificationCheckStatus.Failed &&
            previous?.LastNotifiedStatus != VerificationCheckStatus.Failed)
        {
            notification = CheckNotification.StartedFailing;
        }
        else if (observed != VerificationCheckStatus.Failed &&
                 previous?.LastNotifiedStatus == VerificationCheckStatus.Failed)
        {
            notification = CheckNotification.Recovered;
        }

        var state = new CheckLedgerState(
            Status: observed,
            LastConclusiveStatus: observed,
            LastConclusiveAt: observedAt,
            StatusChangedAt: changed ? observedAt : previous!.StatusChangedAt,
            ConsecutiveFailures: observed == VerificationCheckStatus.Failed
                ? (previous?.ConsecutiveFailures ?? 0) + 1
                : 0,
            ConsecutiveInconclusive: 0,
            LastNotifiedStatus: notification == CheckNotification.None
                ? previous?.LastNotifiedStatus
                : observed,
            LastNotifiedAt: notification == CheckNotification.None
                ? previous?.LastNotifiedAt
                : observedAt);

        return new CheckLedgerTransition(state, notification);
    }

    /// <summary>
    /// The check applies and could not be run. Everything conclusive is preserved untouched — this
    /// observation is evidence about DeployAI's reach, not about the app.
    /// </summary>
    private static CheckLedgerTransition Blind(
        CheckLedgerState? previous,
        DateTimeOffset observedAt,
        int inconclusiveRunsBeforeNotify)
    {
        var consecutive = (previous?.ConsecutiveInconclusive ?? 0) + 1;

        // Exactly at the threshold, so this fires once per blind spell rather than every sweep for as
        // long as it lasts. Only for a check that used to work: one that has never concluded anything
        // has no "went blind" moment to report.
        var notification = previous?.LastConclusiveStatus is not null &&
                           consecutive == inconclusiveRunsBeforeNotify
            ? CheckNotification.WentBlind
            : CheckNotification.None;

        var state = new CheckLedgerState(
            Status: VerificationCheckStatus.Inconclusive,
            LastConclusiveStatus: previous?.LastConclusiveStatus,
            LastConclusiveAt: previous?.LastConclusiveAt,
            StatusChangedAt: previous?.StatusChangedAt ?? observedAt,
            // Deliberately not reset: a failing check that becomes unreadable has not recovered, and
            // zeroing the streak here would make the next real failure look like the first.
            ConsecutiveFailures: previous?.ConsecutiveFailures ?? 0,
            ConsecutiveInconclusive: consecutive,
            // Never touched. If an inconclusive run could clear this, a blip mid-outage would send
            // "recovered" and the outage would then be announced twice.
            LastNotifiedStatus: previous?.LastNotifiedStatus,
            LastNotifiedAt: previous?.LastNotifiedAt);

        return new CheckLedgerTransition(state, notification);
    }

    /// <summary>
    /// The check no longer applies here at all. Streaks reset — a stale failure count for something
    /// that is not being measured any more would misreport the next real one — but nothing is
    /// announced, because "we stopped checking" is not a recovery.
    /// </summary>
    private static CheckLedgerTransition NoLongerApplies(CheckLedgerState? previous, DateTimeOffset observedAt)
    {
        var state = new CheckLedgerState(
            Status: VerificationCheckStatus.Skipped,
            LastConclusiveStatus: previous?.LastConclusiveStatus,
            LastConclusiveAt: previous?.LastConclusiveAt,
            StatusChangedAt: previous?.StatusChangedAt ?? observedAt,
            ConsecutiveFailures: 0,
            ConsecutiveInconclusive: 0,
            LastNotifiedStatus: previous?.LastNotifiedStatus,
            LastNotifiedAt: previous?.LastNotifiedAt);

        return new CheckLedgerTransition(state, CheckNotification.None);
    }
}
