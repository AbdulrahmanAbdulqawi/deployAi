namespace DeployAI.Core.Domains;

/// <summary>The state a domain should move to, and what to tell the user about it.</summary>
/// <param name="ConclusiveStatus">
/// Set only when the check that produced this transition actually concluded something. A run of
/// inconclusive checks leaves it null, which is what stops a deadline from being reported as a DNS
/// failure the user is expected to go and fix.
/// </param>
public sealed record DomainTransition(
    DomainLifecycleState State,
    string Message,
    bool IsTerminal,
    DomainLifecycleState? ConclusiveStatus = null);

/// <summary>
/// Mirrors the persisted status enum. Duplicated in Core rather than referenced because Core does
/// not depend on the data layer, and the transition rules are the part worth testing in isolation.
/// </summary>
public enum DomainLifecycleState
{
    Pending = 0,
    DnsPending = 1,
    DnsVerified = 2,
    Assigned = 3,
    CertificatePending = 4,
    Active = 5,
    DnsFailed = 6,
    DnsUnverifiable = 7,
    CertificateFailed = 8,
    CertificateUnverifiable = 9,
    Conflicted = 10,
    Retired = 11
}

/// <summary>
/// The rules deciding how far a domain has got. Pure: given a check and whether the clock has run
/// out, there is exactly one right answer, and it does not depend on anything that has to be
/// mocked.
/// </summary>
public static class DomainTransitions
{
    /// <summary>
    /// What a DNS check means for a domain still waiting on its record.
    /// </summary>
    /// <param name="deadlinePassed">
    /// Whether waiting has run out. Only ever turns a "keep waiting" into a terminal state — it
    /// never changes which terminal state, because that is decided by what was actually observed.
    /// </param>
    /// <param name="lastConclusiveState">
    /// The most recent check that concluded anything, or null if none ever did. This is what
    /// separates "we checked, and your record is wrong" from "we never managed to check".
    /// </param>
    public static DomainTransition AfterDnsCheck(
        DnsCheckResult check,
        bool deadlinePassed,
        DomainLifecycleState? lastConclusiveState)
    {
        // A CAA record that excludes Let's Encrypt makes every certificate attempt fail, so there
        // is nothing to wait for. Failing now costs one clear message; waiting costs five spent
        // validations an hour and ends in the same place.
        if (!check.IsInconclusive && check.BlocksLetsEncrypt)
        {
            return new DomainTransition(
                DomainLifecycleState.DnsFailed,
                Describe(check),
                IsTerminal: true,
                DomainLifecycleState.DnsFailed);
        }

        if (check.PointsAtTarget)
        {
            return new DomainTransition(
                DomainLifecycleState.DnsVerified,
                Describe(check),
                IsTerminal: false,
                DomainLifecycleState.DnsVerified);
        }

        if (check.IsInconclusive)
        {
            if (!deadlinePassed)
            {
                return new DomainTransition(DomainLifecycleState.DnsPending, Describe(check), false);
            }

            // Never invent a verdict the checks did not reach. If something did conclude earlier,
            // that stands; if nothing ever did, say so rather than blaming the domain.
            return lastConclusiveState is null
                ? new DomainTransition(
                    DomainLifecycleState.DnsUnverifiable,
                    "Could not check where this domain points. Nothing is known to be wrong with it — " +
                    "this is worth retrying.",
                    IsTerminal: true)
                : new DomainTransition(
                    DomainLifecycleState.DnsFailed, Describe(check), IsTerminal: true, lastConclusiveState);
        }

        return deadlinePassed
            ? new DomainTransition(
                DomainLifecycleState.DnsFailed, Describe(check), IsTerminal: true, DomainLifecycleState.DnsFailed)
            : new DomainTransition(
                DomainLifecycleState.DnsPending, Describe(check), IsTerminal: false, DomainLifecycleState.DnsFailed);
    }

    /// <summary>What a certificate inspection means for a domain whose route already exists.</summary>
    public static DomainTransition AfterCertificateCheck(
        CertificateInspection inspection,
        bool deadlinePassed)
    {
        if (inspection.IsIssued)
        {
            return new DomainTransition(
                DomainLifecycleState.Active,
                Describe(inspection),
                IsTerminal: true,
                DomainLifecycleState.Active);
        }

        if (!deadlinePassed)
        {
            return new DomainTransition(
                DomainLifecycleState.CertificatePending, Describe(inspection), IsTerminal: false);
        }

        // The same split as DNS: a handshake that never completed is not evidence the certificate
        // failed, only that nothing could be seen.
        return inspection.IsInconclusive
            ? new DomainTransition(
                DomainLifecycleState.CertificateUnverifiable,
                "Could not reach this domain over HTTPS to see whether its certificate was issued.",
                IsTerminal: true)
            : new DomainTransition(
                DomainLifecycleState.CertificateFailed,
                Describe(inspection),
                IsTerminal: true,
                DomainLifecycleState.CertificateFailed);
    }

    /// <summary>
    /// Whether a domain in this state may be written to the provider with an https:// scheme.
    /// </summary>
    /// <remarks>
    /// The single gate the whole design rests on. An https:// FQDN makes the proxy start a
    /// certificate challenge immediately; against a domain that does not resolve here yet, that
    /// challenge fails, spends one of Let's Encrypt's five failed validations an hour, and leaves a
    /// self-signed certificate behind a deploy that reported success. Every path to assignment goes
    /// through this, so no caller can route around it.
    /// </remarks>
    public static bool MayRequestCertificate(DomainLifecycleState state) =>
        state is DomainLifecycleState.DnsVerified
            or DomainLifecycleState.Assigned
            or DomainLifecycleState.CertificatePending
            or DomainLifecycleState.Active;

    public static bool IsTerminal(DomainLifecycleState state) =>
        state is DomainLifecycleState.Active
            or DomainLifecycleState.DnsFailed
            or DomainLifecycleState.DnsUnverifiable
            or DomainLifecycleState.CertificateFailed
            or DomainLifecycleState.CertificateUnverifiable
            or DomainLifecycleState.Conflicted
            or DomainLifecycleState.Retired;

    private static string Describe(DnsCheckResult check) =>
        check.Findings.Count > 0 ? string.Join(" ", check.Findings) : "No DNS findings were recorded.";

    private static string Describe(CertificateInspection inspection) =>
        inspection.Findings.Count > 0
            ? string.Join(" ", inspection.Findings)
            : "No certificate findings were recorded.";
}
