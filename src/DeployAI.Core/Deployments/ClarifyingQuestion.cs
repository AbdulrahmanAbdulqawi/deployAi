namespace DeployAI.Core.Deployments;

public sealed record ClarifyingQuestionOption(
    string Id,
    string Label,
    string Description,
    IReadOnlyList<DeploymentPlanPart> ResolvesToParts);

public sealed record ClarifyingQuestion(
    string Prompt,
    IReadOnlyList<ClarifyingQuestionOption> Options);
