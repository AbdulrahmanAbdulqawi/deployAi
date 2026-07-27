namespace DeployAI.Core.Deployments;

/// <summary>
/// A repo's classified deployment shape - one <see cref="DeploymentPlanPart"/> per
/// website/server/database piece, plus how confident the classification is and, if ambiguous, a
/// <see cref="ClarifyingQuestion"/> to ask the user instead of guessing.
/// </summary>
public sealed record DeploymentPlan(
    IReadOnlyList<DeploymentPlanPart> Parts,
    string Confidence,
    string PlainSummary,
    ClarifyingQuestion? ClarifyingQuestion = null,
    DeploymentPlanKind PlanKind = DeploymentPlanKind.Default);
