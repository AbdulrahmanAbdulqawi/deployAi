using DeployAI.Core.Deployments;
using DeployAI.Data.Entities;

namespace DeployAI.Api.Services;

/// <summary>Maps a project's raw deploy targets into the UI-friendly application/data service split used by the services list and settings pages.</summary>
public static class ProjectServiceMapper
{
    /// <summary>Splits a project's deploy targets into application vs. data services and computes the Railway/Postgres/Redis inclusion flags.</summary>
    public static ProjectServicesResponse MapProjectServices(Project project)
    {
        var applicationServices = new List<ProjectServiceView>();
        var dataServices = new List<ProjectServiceView>();
        var hasManagedServer = false;
        var includePostgres = false;
        var includeRedis = false;

        foreach (var target in project.DeployTargets)
        {
            var config = DeployTargetConfig.Parse(target.ConfigJson);
            if (config.IsDatabaseTarget)
            {
                dataServices.Add(MapTarget(target, config));
                continue;
            }

            applicationServices.Add(MapTarget(target, config));
            if (SupportsManagedDatabases(target.ProviderName))
            {
                hasManagedServer = true;
                includePostgres = includePostgres || config.IncludePostgres;
                includeRedis = includeRedis || config.IncludeRedis;
            }
        }

        return new ProjectServicesResponse(
            applicationServices,
            dataServices,
            hasManagedServer,
            includePostgres,
            includeRedis);
    }

    // Providers that can provision Postgres/Redis for an app. Coolify gained this in v8
    // (CoolifyProvider.Database), so its database management must surface too.
    private static bool SupportsManagedDatabases(string providerName) =>
        string.Equals(providerName, "railway", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(providerName, "coolify", StringComparison.OrdinalIgnoreCase);

    private static ProjectServiceView MapTarget(DeployTarget target, DeployTargetConfig config)
    {
        var isDatabase = config.IsDatabaseTarget;
        var canManage = !isDatabase && SupportsManagedDatabases(target.ProviderName);
        var linkedKeys = isDatabase ? GetLinkedConnectionKeys(config.DatabaseEngine) : [];

        return new ProjectServiceView(
            target.Id,
            target.ProviderName,
            target.CredentialId,
            target.ProviderProjectId,
            config.Role,
            config.DatabaseEngine,
            GetDisplayName(target, config),
            config.RailwayProjectId,
            linkedKeys,
            canManage,
            config.ServiceDirectory,
            config.RootDirectory);
    }

    private static string GetDisplayName(DeployTarget target, DeployTargetConfig config)
    {
        if (config.IsDatabaseTarget)
        {
            return config.LinkedServiceName ?? DefaultDatabaseServiceName(config.DatabaseEngine);
        }

        if (string.Equals(config.Role, "website", StringComparison.OrdinalIgnoreCase))
        {
            return "Website";
        }

        if (string.Equals(config.Role, "server", StringComparison.OrdinalIgnoreCase))
        {
            return "Server";
        }

        if (string.Equals(target.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase))
        {
            return "Website";
        }

        if (string.Equals(target.ProviderName, "railway", StringComparison.OrdinalIgnoreCase))
        {
            return "Server";
        }

        return target.ProviderName;
    }

    private static string DefaultDatabaseServiceName(string? engine) =>
        engine switch
        {
            "postgres" => "Postgres",
            "redis" => "Redis",
            _ => "Database"
        };

    private static IReadOnlyList<string> GetLinkedConnectionKeys(string? engine) =>
        engine switch
        {
            "postgres" => ["ConnectionStrings__Default", "ConnectionStrings__DefaultConnection"],
            "redis" => ["ConnectionStrings__Redis"],
            _ => []
        };
}
