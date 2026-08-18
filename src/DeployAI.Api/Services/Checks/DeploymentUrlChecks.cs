using DeployAI.Core.Deployments;
using DeployAI.Data.Entities;

namespace DeployAI.Api.Services.Checks;

/// <summary>
/// The existing live-URL probes — homepage, SPA shell, API health, CORS, proxied login — brought
/// into the project-scoped vocabulary.
/// </summary>
/// <remarks>
/// Wraps <see cref="IDeploymentVerificationService"/> rather than replacing it: the same checks run
/// when a user clicks "verify" on a deployment, and having two implementations of what a healthy
/// website looks like is how they drift apart.
/// </remarks>
public sealed class DeploymentUrlChecks : IProjectCheckContributor
{
    private readonly IDeploymentVerificationService _verification;

    public DeploymentUrlChecks(IDeploymentVerificationService verification)
    {
        _verification = verification;
    }

    public string Name => "live URLs";

    public async Task<IReadOnlyList<ProjectVerificationCheck>> ContributeAsync(
        ProjectCheckContext context,
        CancellationToken cancellationToken)
    {
        if (context.DeploymentId is not { } deploymentId)
        {
            // Not a skip. A skip says the check does not apply; this project simply has no live URL
            // to probe yet, and saying so is the difference between "checked and fine" and "never
            // got far enough to check".
            return
            [
                new ProjectVerificationCheck(
                    "deployment.never_succeeded",
                    VerificationCheckTargets.Project,
                    "Live URLs",
                    VerificationCheckStatus.Inconclusive,
                    "This app has not completed a publish, so it has no live address to check yet.")
            ];
        }

        var result = await _verification.VerifyAsync(
            deploymentId, DeploymentVerificationScope.Both, cancellationToken);

        return result.Checks
            .Select(c => new ProjectVerificationCheck(
                c.Id,
                c.Target,
                c.Label,
                MapStatus(c.Status),
                c.Message,
                c.Url,
                c.SuggestedAction))
            .ToList();
    }

    /// <summary>
    /// Lifts the probes' four string statuses into the five-value vocabulary.
    /// </summary>
    /// <remarks>
    /// Nothing maps to <see cref="VerificationCheckStatus.Inconclusive"/> except a status this code
    /// does not recognise, and that is a known hole rather than an oversight: the probes report a
    /// host they could not reach as <c>failed</c>, so a TLS handshake failure and a genuinely broken
    /// app are the same answer to them. Widening <c>ProbeCheckStatus</c> reaches into the Claude-fix
    /// eligibility rules and the verification panel, so it is recorded as a gap and left for its own
    /// change rather than smuggled in here.
    /// </remarks>
    private static VerificationCheckStatus MapStatus(string status) => status switch
    {
        "passed" => VerificationCheckStatus.Passed,
        "failed" => VerificationCheckStatus.Failed,
        "warning" => VerificationCheckStatus.Warning,
        "skipped" => VerificationCheckStatus.Skipped,
        _ => VerificationCheckStatus.Inconclusive
    };
}
