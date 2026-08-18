using System.Text;

namespace DeployAI.Core.Deployments;

/// <summary>
/// One check's answer about one project, in the vocabulary that distinguishes "failing" from
/// "could not check".
/// </summary>
/// <param name="CheckId">
/// Stable across runs — history is grouped by it. Multi-pair projects prefix it with the provider
/// pair, matching what <c>DeploymentVerificationService</c> already does.
/// </param>
/// <param name="DeployTargetId">Which part of the project this was about; null for project-wide checks.</param>
public sealed record ProjectVerificationCheck(
    string CheckId,
    string Target,
    string Label,
    VerificationCheckStatus Status,
    string Message,
    string? Url = null,
    string? SuggestedAction = null,
    Guid? DeployTargetId = null);

/// <summary>How a set of check answers becomes one word for the project.</summary>
/// <remarks>
/// Pure so the interesting case can be tested without a database or a provider: every check
/// inconclusive must roll up to <see cref="ProjectHealthStatus.Inconclusive"/>. The rollup this
/// replaced returned <see cref="ProjectHealthStatus.Unknown"/> there, which the UI renders as "not
/// checked yet" — so a project DeployAI had entirely lost sight of was indistinguishable from a
/// project it had never looked at.
/// </remarks>
public static class ProjectHealthRollup
{
    public static (ProjectHealthStatus Status, string Summary) Roll(
        IReadOnlyList<ProjectVerificationCheck> checks)
    {
        var passed = Count(checks, VerificationCheckStatus.Passed);
        var warning = Count(checks, VerificationCheckStatus.Warning);
        var failed = Count(checks, VerificationCheckStatus.Failed);
        var inconclusive = Count(checks, VerificationCheckStatus.Inconclusive);
        var conclusive = passed + warning + failed;

        var status = failed switch
        {
            > 0 when failed == conclusive => ProjectHealthStatus.Down,
            > 0 => ProjectHealthStatus.Degraded,
            _ when conclusive > 0 => ProjectHealthStatus.Healthy,
            _ when inconclusive > 0 => ProjectHealthStatus.Inconclusive,
            _ => ProjectHealthStatus.Unknown
        };

        return (status, BuildSummary(checks, passed, warning, failed, inconclusive, conclusive));
    }

    private static string BuildSummary(
        IReadOnlyList<ProjectVerificationCheck> checks,
        int passed,
        int warning,
        int failed,
        int inconclusive,
        int conclusive)
    {
        if (checks.Count == 0)
        {
            return "Nothing to check yet.";
        }

        if (failed == 0 && inconclusive == 0 && warning == 0 && passed > 0)
        {
            return passed == 1 ? "The one check that applies passed." : $"All {passed} checks passed.";
        }

        var summary = new StringBuilder();
        summary.Append($"{passed} of {conclusive + inconclusive} checks passed");

        if (warning > 0)
        {
            summary.Append($"; {warning} warned");
        }

        if (failed > 0)
        {
            summary.Append($"; {failed} failed");
        }

        if (inconclusive > 0)
        {
            // The reason belongs in the sentence, not one click away: "2 could not be checked" is a
            // shrug, and "2 could not be checked (Coolify is unreachable)" is something to act on.
            summary.Append($"; {inconclusive} could not be checked");
            var reason = FirstReason(checks);
            if (reason is not null)
            {
                summary.Append($" ({reason})");
            }
        }

        summary.Append('.');
        return Truncate(summary.ToString(), 1024);
    }

    private static string? FirstReason(IReadOnlyList<ProjectVerificationCheck> checks)
    {
        foreach (var check in checks)
        {
            if (check.Status == VerificationCheckStatus.Inconclusive &&
                !string.IsNullOrWhiteSpace(check.Message))
            {
                return Truncate(check.Message.TrimEnd('.'), 160);
            }
        }

        return null;
    }

    private static int Count(IReadOnlyList<ProjectVerificationCheck> checks, VerificationCheckStatus status)
    {
        var count = 0;
        foreach (var check in checks)
        {
            if (check.Status == status)
            {
                count++;
            }
        }

        return count;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
