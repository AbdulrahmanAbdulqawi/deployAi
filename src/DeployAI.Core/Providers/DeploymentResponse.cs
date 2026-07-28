namespace DeployAI.Core.Providers;

/// <summary>The result of triggering a deployment: the provider's deployment id and, if known immediately, its URL.</summary>
public sealed record DeploymentResponse(string DeploymentId, string? DeployUrl);
