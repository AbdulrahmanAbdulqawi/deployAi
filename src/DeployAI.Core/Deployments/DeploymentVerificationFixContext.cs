namespace DeployAI.Core.Deployments;

/// <summary>The failed verification check details passed to Claude when generating a targeted verification fix.</summary>
public sealed record DeploymentVerificationFixContext(
    string CheckId,
    string CheckTarget,
    string Label,
    string Message,
    string? Url,
    string? WebsiteUrl,
    string? ApiUrl);
