using DeployAI.Core.Deployments;

namespace DeployAI.Data.Entities;

/// <summary>What caused a verification run. Kept as a string because it is a label, not a state machine.</summary>
public static class VerificationRunTriggers
{
    /// <summary>The recurring fleet sweep.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Someone pressed a button.</summary>
    public const string Manual = "manual";

    /// <summary>Ran off the back of a deployment finishing.</summary>
    public const string PostDeploy = "post_deploy";
}

/// <summary>
/// One project's verification, once. The unit of history.
/// </summary>
/// <remarks>
/// Verification results used to be computed and thrown away, leaving only a coarse rollup on the
/// project. That made the one question worth asking unanswerable: not "is this check failing" but
/// "did it pass yesterday". A regression was invisible until a person opened a browser.
/// </remarks>
public class ProjectVerificationRun
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>
    /// The deployment whose live URLs were probed. Null when none qualified — a project that has
    /// never completed a publish is still checked for everything that does not need a URL.
    /// </summary>
    public Guid? DeploymentId { get; set; }

    /// <summary>One of <see cref="VerificationRunTriggers"/>.</summary>
    public string Trigger { get; set; } = VerificationRunTriggers.Scheduled;

    public ProjectHealthStatus Outcome { get; set; } = ProjectHealthStatus.Unknown;

    /// <summary>
    /// Whether the sweep itself broke on this project, as opposed to the project's checks concluding
    /// nothing. Both leave the user without an answer, but only one of them is our bug — and a
    /// monitor that cannot count how often it failed at its own job will not get fixed.
    /// </summary>
    public bool SweepErrored { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset CompletedAt { get; set; }

    public int DurationMs { get; set; }

    public int PassedChecks { get; set; }
    public int FailedChecks { get; set; }
    public int WarningChecks { get; set; }
    public int SkippedChecks { get; set; }
    public int InconclusiveChecks { get; set; }

    /// <summary>One line a person reads. Never an exception's text.</summary>
    public string Summary { get; set; } = string.Empty;

    public Project Project { get; set; } = null!;

    public ICollection<ProjectVerificationCheckResult> Results { get; set; } = [];
}
