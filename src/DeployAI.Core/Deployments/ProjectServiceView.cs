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
    bool HasRailwayServer,
    bool IncludePostgres,
    bool IncludeRedis);
