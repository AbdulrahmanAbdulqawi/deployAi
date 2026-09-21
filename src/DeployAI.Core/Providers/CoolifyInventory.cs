namespace DeployAI.Core.Providers;

/// <summary>
/// Everything a Coolify instance is running, as Coolify itself organises it: projects holding
/// environments holding applications and databases. Read-only.
///
/// This exists because answering "what does this instance run, and how is each app built?" used
/// to mean opening every project, environment and application page in Coolify's UI by hand —
/// forty page reads for one instance — which the core rule names as a gap. It is the same four
/// GETs the provider already makes for other reasons, joined on Coolify's numeric environment id.
/// </summary>
public sealed record CoolifyInventory(
    IReadOnlyList<CoolifyInventoryProject> Projects,
    /// <summary>
    /// Applications whose <c>environment_id</c> matched no listed environment. Kept rather than
    /// dropped: an app that cannot be placed is still an app that exists, and silently omitting
    /// it would make the inventory claim the instance runs fewer apps than it does.
    /// </summary>
    IReadOnlyList<CoolifyInventoryApplication> UnplacedApplications,
    IReadOnlyList<CoolifyInventoryDatabase> UnplacedDatabases);

public sealed record CoolifyInventoryProject(
    string Uuid,
    string Name,
    IReadOnlyList<CoolifyInventoryEnvironment> Environments,
    /// <summary>
    /// True when Coolify refused to list this project's environments, as opposed to listing none.
    /// "Could not look" and "found nothing" are different answers, and a project shown as empty
    /// because its listing failed would read as a project with nothing in it.
    /// </summary>
    bool EnvironmentsInconclusive);

public sealed record CoolifyInventoryEnvironment(
    string Uuid,
    string Name,
    IReadOnlyList<CoolifyInventoryApplication> Applications,
    IReadOnlyList<CoolifyInventoryDatabase> Databases);

public sealed record CoolifyInventoryApplication(
    string Uuid,
    string Name,
    /// <summary>Coolify's build pack: <c>nixpacks</c>, <c>static</c>, <c>dockerfile</c>, <c>dockercompose</c>.</summary>
    string? BuildPack,
    /// <summary>Coolify's own status string, e.g. <c>running:healthy</c> or <c>exited:unhealthy</c>. Passed through, not interpreted.</summary>
    string? Status,
    string? GitRepository,
    string? GitBranch,
    /// <summary>Every hostname routed to this app — the top-level fqdn list, or per-service compose domains.</summary>
    IReadOnlyList<string> Domains);

public sealed record CoolifyInventoryDatabase(
    string Uuid,
    string Name,
    /// <summary>Coolify's engine identifier, e.g. <c>standalone-postgresql</c>.</summary>
    string? Engine,
    string? Status);
