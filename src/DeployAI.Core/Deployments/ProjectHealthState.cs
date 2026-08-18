using System.Text.Json;

namespace DeployAI.Core.Deployments;

/// <summary>A project's overall live health, rolled up from its most recent verification run.</summary>
/// <remarks>
/// Appended to, never reordered: the value is persisted as an integer inside <c>Project.HealthJson</c>,
/// so 0-3 must keep the meaning they already have in every stored blob.
/// </remarks>
public enum ProjectHealthStatus
{
    Healthy = 0,
    Degraded = 1,
    Down = 2,

    /// <summary>Never checked. The absence of a run, not the result of one.</summary>
    Unknown = 3,

    /// <summary>
    /// Checked, and nothing was learned — every check that applied could not be run. Distinct from
    /// <see cref="Unknown"/>, which means no run has happened, and from <see cref="Down"/>, which is a
    /// conclusion. Collapsing this into either one reports a blind monitor as a working one.
    /// </summary>
    Inconclusive = 4
}

/// <summary>
/// A project's last recorded health, persisted as JSON on <c>Project.HealthJson</c>.
/// </summary>
/// <remarks>
/// A derived cache, not the record: the rows in <c>project_verification_runs</c> and
/// <c>project_check_states</c> are the source of truth. This blob exists so the project list and the
/// health banner can render without a join, and is rewritten in the same save as the rows it
/// summarises. Properties are only ever added — a blob written by an older build must still
/// deserialize, and missing properties take their defaults.
/// </remarks>
public sealed class ProjectHealthState
{
    public DateTimeOffset LastCheckedAt { get; set; }
    public ProjectHealthStatus Status { get; set; } = ProjectHealthStatus.Unknown;
    public int PassedChecks { get; set; }
    public int TotalChecks { get; set; }
    public string? Summary { get; set; }
    public Guid? DeploymentId { get; set; }

    public int FailedChecks { get; set; }
    public int WarningChecks { get; set; }

    /// <summary>Checks that applied and could not be run. The count that keeps a green rollup honest.</summary>
    public int InconclusiveChecks { get; set; }

    public int SkippedChecks { get; set; }

    /// <summary>The verification run this was rolled up from, so the detail is one query away.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Parses a project's stored health-state JSON, returning null if none is stored yet.</summary>
    public static ProjectHealthState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ProjectHealthState>(json);
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
