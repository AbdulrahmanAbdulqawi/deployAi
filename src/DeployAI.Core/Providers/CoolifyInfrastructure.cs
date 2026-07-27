namespace DeployAI.Core.Providers;

/// <summary>A named resource (project, server, or GitHub App) on a Coolify instance.</summary>
public sealed record CoolifyInfrastructureResource(string Id, string Name);

/// <summary>The projects, servers, and GitHub Apps visible on a Coolify instance, as returned to the setup UI.</summary>
public sealed record CoolifyInfrastructureSnapshot(
    IReadOnlyList<CoolifyInfrastructureResource> Projects,
    IReadOnlyList<CoolifyInfrastructureResource> Servers,
    IReadOnlyList<CoolifyInfrastructureResource> GithubApps);
