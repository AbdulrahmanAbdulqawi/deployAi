namespace DeployAI.Core.Deployments;

/// <summary>One selectable answer to a <see cref="ClarifyingQuestion"/>, and the deployment plan it resolves to if chosen.</summary>
public sealed record ClarifyingQuestionOption(
    string Id,
    string Label,
    string Description,
    IReadOnlyList<DeploymentPlanPart> ResolvesToParts);

/// <summary>
/// A question shown to the user when repo classification can't confidently produce a single
/// deployment plan (e.g. ambiguous framework detection) - answering picks one of the offered plans.
/// </summary>
public sealed record ClarifyingQuestion(
    string Prompt,
    IReadOnlyList<ClarifyingQuestionOption> Options);
