namespace DeployAI.Core.Providers;

public sealed record ProviderProject(string Id, string Name, string? Url, string? GitBranch = null);
