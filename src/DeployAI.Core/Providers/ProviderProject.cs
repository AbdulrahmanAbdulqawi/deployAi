namespace DeployAI.Core.Providers;

/// <summary>A project/application as it exists on a provider - id, name, public URL, and tracked branch.</summary>
public sealed record ProviderProject(string Id, string Name, string? Url, string? GitBranch = null);
