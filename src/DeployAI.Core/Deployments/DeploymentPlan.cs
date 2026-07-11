namespace DeployAI.Core.Deployments;

public sealed record DeploymentPlan(
    IReadOnlyList<DeploymentPlanPart> Parts,
    string Confidence,
    string PlainSummary,
    ClarifyingQuestion? ClarifyingQuestion = null,
    DeploymentPlanKind PlanKind = DeploymentPlanKind.Default);
