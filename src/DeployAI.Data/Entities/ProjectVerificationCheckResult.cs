using DeployAI.Core.Deployments;

namespace DeployAI.Data.Entities;

/// <summary>Which part of a project a check was about, so results group the way a person reads them.</summary>
public static class VerificationCheckTargets
{
    public const string Website = "website";
    public const string Server = "server";
    public const string Connection = "connection";
    public const string Provider = "provider";
    public const string Runtime = "runtime";
    public const string Domain = "domain";
    public const string Configuration = "configuration";

    /// <summary>About the project as a whole, or about DeployAI's own ability to check it.</summary>
    public const string Project = "project";
}

/// <summary>One check's answer within one run — the row that makes history queryable.</summary>
public class ProjectVerificationCheckResult
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    /// <summary>
    /// Denormalised from the run on purpose: the question this table exists to answer is "how has
    /// this one check behaved over time", and that query should not have to join to find out which
    /// project it belongs to.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>Which part of the project this was about. Null for project-wide checks.</summary>
    public Guid? DeployTargetId { get; set; }

    /// <summary>
    /// Stable identity for the check across runs, e.g. <c>provider.application_exists</c> or
    /// <c>coolify+coolify:website.reachable</c>. History is grouped by this, so it must not drift
    /// between runs for the same underlying check.
    /// </summary>
    public string CheckId { get; set; } = string.Empty;

    /// <summary>One of <see cref="VerificationCheckTargets"/>.</summary>
    public string Target { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public VerificationCheckStatus Status { get; set; } = VerificationCheckStatus.Skipped;

    /// <summary>
    /// What to tell the user. For an inconclusive result this must say why the check could not run —
    /// "could not look" is only actionable when it names what was in the way.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    public string? Url { get; set; }

    public string? SuggestedAction { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public ProjectVerificationRun Run { get; set; } = null!;
}
