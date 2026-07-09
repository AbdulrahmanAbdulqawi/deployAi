namespace DeployAI.Core.Deployments;

public sealed record DeploymentVerificationFixContext(
    string CheckId,
    string CheckTarget,
    string Label,
    string Message,
    string? Url,
    string? WebsiteUrl,
    string? ApiUrl);
