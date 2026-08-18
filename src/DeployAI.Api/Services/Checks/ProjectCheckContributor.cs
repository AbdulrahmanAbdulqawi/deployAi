using DeployAI.Core.Deployments;
using DeployAI.Data.Entities;

namespace DeployAI.Api.Services.Checks;

/// <summary>Everything a check needs to know about the project it is being asked about.</summary>
/// <param name="DeploymentId">
/// The deployment whose live URLs may be probed, or null when none qualified. Checks that read the
/// provider, the container or the domain need none of this — which is the point: a project whose
/// last publish failed is still fully checkable for whether its app is even there.
/// </param>
public sealed record ProjectCheckContext(
    Project Project,
    IReadOnlyList<DeployTarget> DeployTargets,
    Guid? DeploymentId);

/// <summary>
/// One family of checks that can be asked about a project.
/// </summary>
/// <remarks>
/// Split this way so that adding a kind of check is adding a class and a registration, not editing a
/// method that every other check also lives in. Each contributor is isolated: one that throws costs
/// its own checks (recorded as inconclusive, naming what broke) and not the project's whole run.
/// </remarks>
public interface IProjectCheckContributor
{
    /// <summary>A stable name, used to attribute an inconclusive result when this contributor throws.</summary>
    string Name { get; }

    Task<IReadOnlyList<ProjectVerificationCheck>> ContributeAsync(
        ProjectCheckContext context,
        CancellationToken cancellationToken);
}
