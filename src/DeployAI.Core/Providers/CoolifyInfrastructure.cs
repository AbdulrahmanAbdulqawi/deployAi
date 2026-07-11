namespace DeployAI.Core.Providers;

public sealed record CoolifyInfrastructureResource(string Id, string Name);

public sealed record CoolifyInfrastructureSnapshot(
    IReadOnlyList<CoolifyInfrastructureResource> Projects,
    IReadOnlyList<CoolifyInfrastructureResource> Servers,
    IReadOnlyList<CoolifyInfrastructureResource> GithubApps);
