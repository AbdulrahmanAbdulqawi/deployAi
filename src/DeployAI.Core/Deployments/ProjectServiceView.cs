namespace DeployAI.Core.Deployments;

public sealed record ProjectServiceView(
    Guid Id,
    string ProviderName,
    Guid CredentialId,
    string ProviderProjectId,
    string? Role,
    string? DatabaseEngine,
    string DisplayName,
    string? RailwayProjectId,
    IReadOnlyList<string> LinkedConnectionKeys,
    bool CanManage,
    string? ServiceDirectory,
    string? RootDirectory);

public sealed record ProjectServicesResponse(
    IReadOnlyList<ProjectServiceView> ApplicationServices,
    IReadOnlyList<ProjectServiceView> DataServices,
    // True when a server target runs on a provider that can provision managed databases
    // (Railway or Coolify) — not Railway-only, which hid Coolify's database management.
    bool HasManagedServer,
    bool IncludePostgres,
    bool IncludeRedis);
