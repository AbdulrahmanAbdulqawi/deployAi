using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

/// <summary>
/// How one check's history advances, and what that means for what the user is told.
/// </summary>
/// <remarks>
/// Pure, with no database and no provider, because these rules are the part that is easy to get
/// silently wrong. The dangerous mistake is letting an inconclusive observation touch the ledger: it
/// would send "recovered" the first time the network blipped during a real outage, and announce the
/// same outage twice when it cleared. Mirrors <c>DomainTransitionsTests</c>, whose
/// <c>LastConclusiveStatus</c> this is descended from.
/// </remarks>
public class CheckLedgerTransitionTests
{
    private static readonly DateTimeOffset Monday = new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);
    private const int BlindRunsBeforeNotify = 3;

    private static CheckLedgerTransition Apply(
        CheckLedgerState? previous,
        VerificationCheckStatus observed,
        DateTimeOffset? at = null) =>
        CheckLedgerTransitions.Apply(previous, observed, at ?? Monday, BlindRunsBeforeNotify);

    [Fact]
    public void AFirstFailure_IsATransition()
    {
        var first = Apply(null, VerificationCheckStatus.Failed);

        Assert.Equal(CheckNotification.StartedFailing, first.Notification);
        Assert.Equal(VerificationCheckStatus.Failed, first.State.LastConclusiveStatus);
        Assert.Equal(1, first.State.ConsecutiveFailures);
    }

    /// <summary>A check that is still failing an hour later is not news.</summary>
    [Fact]
    public void FailedThenFailedAgain_IsNotATransition()
    {
        var first = Apply(null, VerificationCheckStatus.Failed);
        var second = Apply(first.State, VerificationCheckStatus.Failed, Monday.AddHours(1));

        Assert.Equal(CheckNotification.None, second.Notification);
        Assert.Equal(2, second.State.ConsecutiveFailures);
        // Still the same failure, so the clock on it does not restart.
        Assert.Equal(first.State.StatusChangedAt, second.State.StatusChangedAt);
    }

    [Fact]
    public void FailedThenPassed_IsARecovery()
    {
        var failed = Apply(null, VerificationCheckStatus.Failed);
        var passed = Apply(failed.State, VerificationCheckStatus.Passed, Monday.AddHours(2));

        Assert.Equal(CheckNotification.Recovered, passed.Notification);
        Assert.Equal(0, passed.State.ConsecutiveFailures);
        Assert.Equal(Monday.AddHours(2), passed.State.StatusChangedAt);
    }

    /// <summary>
    /// The single most important rule here: an unreadable check leaves everything conclusive alone.
    /// </summary>
    [Fact]
    public void FailedThenInconclusive_LeavesLastConclusiveStatusFailed()
    {
        var failed = Apply(null, VerificationCheckStatus.Failed);
        var blind = Apply(failed.State, VerificationCheckStatus.Inconclusive, Monday.AddHours(1));

        Assert.Equal(VerificationCheckStatus.Inconclusive, blind.State.Status);
        Assert.Equal(VerificationCheckStatus.Failed, blind.State.LastConclusiveStatus);
        Assert.Equal(failed.State.LastConclusiveAt, blind.State.LastConclusiveAt);
        Assert.Equal(failed.State.StatusChangedAt, blind.State.StatusChangedAt);
        // A failing check that became unreadable has not recovered, so the streak is preserved.
        Assert.Equal(1, blind.State.ConsecutiveFailures);
    }

    /// <summary>
    /// If a blip cleared the notification ledger, the outage would be announced a second time when
    /// the provider came back — and a "recovered" would go out in between saying nothing recovered.
    /// </summary>
    [Fact]
    public void FailedThenInconclusiveThenFailed_NotifiesExactlyOnce()
    {
        var failed = Apply(null, VerificationCheckStatus.Failed);
        var blind = Apply(failed.State, VerificationCheckStatus.Inconclusive, Monday.AddHours(1));
        var failedAgain = Apply(blind.State, VerificationCheckStatus.Failed, Monday.AddHours(2));

        Assert.Equal(CheckNotification.StartedFailing, failed.Notification);
        Assert.Equal(CheckNotification.None, blind.Notification);
        Assert.Equal(CheckNotification.None, failedAgain.Notification);
    }

    /// <summary>Recovery is measured against what the user was actually told, not the last observation.</summary>
    [Fact]
    public void FailedThenInconclusiveThenPassed_IsStillARecovery()
    {
        var failed = Apply(null, VerificationCheckStatus.Failed);
        var blind = Apply(failed.State, VerificationCheckStatus.Inconclusive, Monday.AddHours(1));
        var passed = Apply(blind.State, VerificationCheckStatus.Passed, Monday.AddHours(2));

        Assert.Equal(CheckNotification.Recovered, passed.Notification);
    }

    [Fact]
    public void StatusChangedAt_MovesOnlyOnAConclusiveChange()
    {
        var passed = Apply(null, VerificationCheckStatus.Passed);
        var stillPassing = Apply(passed.State, VerificationCheckStatus.Passed, Monday.AddHours(1));
        var blind = Apply(stillPassing.State, VerificationCheckStatus.Inconclusive, Monday.AddHours(2));
        var failed = Apply(blind.State, VerificationCheckStatus.Failed, Monday.AddHours(3));

        Assert.Equal(Monday, stillPassing.State.StatusChangedAt);
        Assert.Equal(Monday, blind.State.StatusChangedAt);
        Assert.Equal(Monday.AddHours(3), failed.State.StatusChangedAt);
    }

    /// <summary>
    /// A monitor that quietly goes blind is the failure the inconclusive distinction exists to
    /// prevent, so the silence gets reported — once per blind spell, not once per sweep.
    /// </summary>
    [Fact]
    public void GoingBlindForLongEnough_RaisesItsOwnNotificationExactlyOnce()
    {
        var state = Apply(null, VerificationCheckStatus.Passed).State;
        var notifications = new List<CheckNotification>();

        for (var run = 1; run <= 6; run++)
        {
            var transition = Apply(state, VerificationCheckStatus.Inconclusive, Monday.AddHours(run));
            state = transition.State;
            notifications.Add(transition.Notification);
        }

        Assert.Equal(1, notifications.Count(n => n == CheckNotification.WentBlind));
        // On the third consecutive blind run, matching the configured threshold.
        Assert.Equal(CheckNotification.WentBlind, notifications[BlindRunsBeforeNotify - 1]);
        Assert.Equal(6, state.ConsecutiveInconclusive);
    }

    /// <summary>
    /// A check that has never concluded anything has no "went blind" moment: it was never sighted,
    /// so reporting that DeployAI has stopped seeing it would be telling the user about a capability
    /// they never had.
    /// </summary>
    [Fact]
    public void ACheckThatNeverConcluded_DoesNotReportGoingBlind()
    {
        CheckLedgerState? state = null;
        var notifications = new List<CheckNotification>();

        for (var run = 1; run <= 5; run++)
        {
            var transition = Apply(state, VerificationCheckStatus.Inconclusive, Monday.AddHours(run));
            state = transition.State;
            notifications.Add(transition.Notification);
        }

        Assert.All(notifications, n => Assert.Equal(CheckNotification.None, n));
    }

    /// <summary>
    /// A check that stops applying resets its streaks but announces nothing: "we stopped checking"
    /// is not a recovery.
    /// </summary>
    [Fact]
    public void BecomingSkipped_ResetsStreaksWithoutAnnouncingARecovery()
    {
        var failed = Apply(null, VerificationCheckStatus.Failed);
        var skipped = Apply(failed.State, VerificationCheckStatus.Skipped, Monday.AddHours(1));

        Assert.Equal(CheckNotification.None, skipped.Notification);
        Assert.Equal(0, skipped.State.ConsecutiveFailures);
        Assert.Equal(VerificationCheckStatus.Failed, skipped.State.LastConclusiveStatus);
    }

    /// <summary>A warning after a failure still counts as no longer failing.</summary>
    [Fact]
    public void FailedThenWarning_IsARecovery()
    {
        var failed = Apply(null, VerificationCheckStatus.Failed);
        var warning = Apply(failed.State, VerificationCheckStatus.Warning, Monday.AddHours(1));

        Assert.Equal(CheckNotification.Recovered, warning.Notification);
    }

    /// <summary>A first pass is not something to email about.</summary>
    [Fact]
    public void AFirstPass_NotifiesNothing()
    {
        var passed = Apply(null, VerificationCheckStatus.Passed);

        Assert.Equal(CheckNotification.None, passed.Notification);
        Assert.Null(passed.State.LastNotifiedStatus);
    }
}
