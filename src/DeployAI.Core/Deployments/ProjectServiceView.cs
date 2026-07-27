namespace DeployAI.Core.Deployments;

/// <summary>A project's deploy target mapped to a UI-friendly view - app or data service, display name, and manageability.</summary>
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

/// <summary>A project's services split into application vs. data services, plus flags on what's included.</summary>
public sealed record ProjectServicesResponse(
    IReadOnlyList<ProjectServiceView> ApplicationServices,
    IReadOnlyList<ProjectServiceView> DataServices,
    bool HasRailwayServer,
    bool IncludePostgres,
    bool IncludeRedis);
