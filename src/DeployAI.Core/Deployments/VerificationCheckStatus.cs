namespace DeployAI.Core.Deployments;

/// <summary>
/// What one verification check concluded about a deployed app.
/// </summary>
/// <remarks>
/// <para>
/// The two negative values are deliberately separate, and keeping them separate is the whole reason
/// this type exists. <see cref="Failed"/> means the check ran and the app is wrong.
/// <see cref="Inconclusive"/> means the check could not run — the provider was unreachable, the
/// credential was revoked, the container returned no output. Code that returns one value for both
/// turns a blind scan into a confident negative, and a monitor that cannot tell the difference will
/// report an outage during a network blip and report health while it is blind.
/// </para>
/// <para>
/// <see cref="Skipped"/> is a third, distinct answer: the check does not apply here at all — there is
/// no server target to probe, or the provider exposes no container output to read. "Does not apply"
/// and "applies but could not be run" lead to different actions, so they are different values.
/// </para>
/// <para>
/// The same distinction already exists piecemeal across the codebase — <c>EnvScanResult.IsInconclusive</c>,
/// <c>RepositoryLayout.IsInconclusive</c>, <c>RuntimeExceptionScan.Inconclusive</c>,
/// <c>CertificateInspection.IsInconclusive</c>, <c>ProjectDomain.LastConclusiveStatus</c>. This is that
/// rule given one shared vocabulary.
/// </para>
/// </remarks>
public enum VerificationCheckStatus
{
    /// <summary>The check does not apply to this target. Not evidence of anything.</summary>
    Skipped = 0,

    /// <summary>The check ran and the app is behaving.</summary>
    Passed = 1,

    /// <summary>The check ran and found something worth reporting that is not yet a failure.</summary>
    Warning = 2,

    /// <summary>The check ran and the app is not behaving.</summary>
    Failed = 3,

    /// <summary>
    /// The check applies but could not be run, so nothing was learned. Never treat this as a pass:
    /// the reason belongs in the message, because "could not look" is only useful if it says why.
    /// </summary>
    Inconclusive = 4
}

/// <summary>Helpers for reasoning about a set of check statuses without re-deriving the rules.</summary>
public static class VerificationCheckStatusExtensions
{
    /// <summary>
    /// Whether this status is evidence about the app, as opposed to evidence about DeployAI's reach.
    /// Only conclusive statuses may move a transition ledger or trigger a notification.
    /// </summary>
    public static bool IsConclusive(this VerificationCheckStatus status) =>
        status is VerificationCheckStatus.Passed
            or VerificationCheckStatus.Warning
            or VerificationCheckStatus.Failed;
}
