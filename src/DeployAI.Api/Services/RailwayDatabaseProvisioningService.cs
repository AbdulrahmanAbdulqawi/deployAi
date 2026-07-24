using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeployAI.Api.Services;

public interface IRailwayDatabaseProvisioningService
{
    Task<DatabaseRequirementProfile> DetectRequirementsAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken);

    Task ProvisionAsync(
        Project project,
        DeployTarget serverTarget,
        DatabaseProvisioningRequest request,
        CancellationToken cancellationToken);

    Task EnsureFromRepoAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken);

    Task RemoveDatabaseServiceAsync(
        Project project,
        DeployTarget databaseTarget,
        CancellationToken cancellationToken);

    Task TeardownDatabaseServiceOnProviderAsync(
        Project project,
        DeployTarget databaseTarget,
        CancellationToken cancellationToken);
}

public sealed class RailwayDatabaseProvisioningService : IRailwayDatabaseProvisioningService
{
    private readonly DeployAIDbContext _db;
    private readonly IProviderDatabaseProvisioningFactory _provisioningFactory;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IProviderServiceOperationsFactory _serviceOperationsFactory;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IGitHubService _gitHubService;
    private readonly IEncryptionService _encryption;
    private readonly IDatabaseRequirementDetector _databaseRequirementDetector;
    private readonly ILogger<RailwayDatabaseProvisioningService> _logger;

    public RailwayDatabaseProvisioningService(
        DeployAIDbContext db,
        IProviderDatabaseProvisioningFactory provisioningFactory,
        IProviderManagementFactory managementFactory,
        IProviderServiceOperationsFactory serviceOperationsFactory,
        IProviderCredentialTokenService tokens,
        IGitHubService gitHubService,
        IEncryptionService encryption,
        IDatabaseRequirementDetector databaseRequirementDetector,
        ILogger<RailwayDatabaseProvisioningService> logger)
    {
        _db = db;
        _provisioningFactory = provisioningFactory;
        _managementFactory = managementFactory;
        _serviceOperationsFactory = serviceOperationsFactory;
        _tokens = tokens;
        _gitHubService = gitHubService;
        _encryption = encryption;
        _databaseRequirementDetector = databaseRequirementDetector;
        _logger = logger;
    }

    public Task<DatabaseRequirementProfile> DetectRequirementsAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken) =>
        DetectRequirementsInternalAsync(project, serverTarget, branch, cancellationToken);

    public async Task EnsureFromRepoAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken)
    {
        if (_provisioningFactory.GetProvisioning(serverTarget.ProviderName) is null)
        {
            _logger.LogWarning("DB-PROVISION: no provisioning registered for provider {Provider}; skipping.", serverTarget.ProviderName);
            return;
        }

        var profile = await DetectRequirementsInternalAsync(project, serverTarget, branch, cancellationToken);
        _logger.LogInformation(
            "DB-PROVISION: detection for target {TargetId} on branch {Branch}: postgres={Pg} redis={Redis} keys=[{Keys}] pgName={PgName}",
            serverTarget.Id, branch, profile.RequiresPostgres, profile.RequiresRedis,
            string.Join(",", profile.ConnectionStringKeys ?? []), profile.PostgresDatabaseName);
        if (!profile.RequiresPostgres && !profile.RequiresRedis)
        {
            return;
        }

        await ProvisionAsync(
            project,
            serverTarget,
            new DatabaseProvisioningRequest(
                profile.RequiresPostgres,
                profile.RequiresRedis,
                profile.PostgresDatabaseName,
                profile.ConnectionStringKeys),
            cancellationToken);
    }

    private async Task<DatabaseRequirementProfile> DetectRequirementsInternalAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken)
    {
        var parts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return new DatabaseRequirementProfile(false, false, []);
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var gitHubToken = _encryption.Decrypt(user.GitHubTokenEncrypted);
        var serverConfig = DeployTargetConfig.Parse(serverTarget.ConfigJson);
        var serverPath = (serverConfig.ServiceDirectory ?? serverConfig.RootDirectory ?? string.Empty).Trim().Trim('/');

        var dockerCompose = await ReadFirstExistingFileAsync(
            gitHubToken,
            parts[0],
            parts[1],
            ["docker-compose.yml", "docker-compose.yaml"],
            branch,
            cancellationToken);
        var appsettingsPath = string.IsNullOrEmpty(serverPath)
            ? "appsettings.json"
            : $"{serverPath}/appsettings.json";
        var appsettings = await _gitHubService.GetFileContentAsync(
            gitHubToken,
            parts[0],
            parts[1],
            appsettingsPath,
            branch,
            cancellationToken);

        // A .NET modular monolith's service directory is the build context (e.g. backend/src),
        // but appsettings.json — with the connection strings — lives inside the startup project
        // (backend/src/YemenHub.Api). When it isn't at the service root, resolve it the same
        // recursive way the Dockerfile provisioner finds the entry .csproj, then read the
        // appsettings.json sitting next to that project.
        if (string.IsNullOrWhiteSpace(appsettings))
        {
            appsettings = await ReadNestedAppSettingsAsync(gitHubToken, parts[0], parts[1], serverPath, branch, cancellationToken);
        }

        var profile = _databaseRequirementDetector.Detect(dockerCompose, appsettings);
        return profile;
    }

    private async Task<string?> ReadNestedAppSettingsAsync(
        string gitHubToken,
        string owner,
        string repo,
        string serverPath,
        string branch,
        CancellationToken cancellationToken)
    {
        // ListAllContentsAsync only returns one directory level, so walk down manually (bounded to
        // two levels — deep enough for a .NET startup project nested under a build-context folder
        // like backend/src/YemenHub.Api, shallow enough to stay a couple of API calls).
        var path = await FindNestedAppSettingsPathAsync(gitHubToken, owner, repo, serverPath, branch, depth: 2, cancellationToken);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return await _gitHubService.GetFileContentAsync(gitHubToken, owner, repo, path, branch, cancellationToken);
    }

    private async Task<string?> FindNestedAppSettingsPathAsync(
        string gitHubToken,
        string owner,
        string repo,
        string directory,
        string branch,
        int depth,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GitHubContentItem> items;
        try
        {
            items = await _gitHubService.ListAllContentsAsync(gitHubToken, owner, repo, directory, branch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DB-PROVISION: could not list contents under '{Path}' to find nested appsettings.", directory);
            return null;
        }

        var appsettingsHere = items.FirstOrDefault(item =>
            string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name, "appsettings.json", StringComparison.OrdinalIgnoreCase));

        // The startup project's appsettings.json sits next to a .csproj — prefer that pairing.
        var hasCsproj = items.Any(item =>
            string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase) &&
            item.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (appsettingsHere is not null && hasCsproj)
        {
            return appsettingsHere.Path;
        }

        if (depth > 0)
        {
            foreach (var subdirectory in items.Where(item =>
                         string.Equals(item.Type, "dir", StringComparison.OrdinalIgnoreCase)))
            {
                var found = await FindNestedAppSettingsPathAsync(
                    gitHubToken, owner, repo, subdirectory.Path, branch, depth - 1, cancellationToken);
                if (!string.IsNullOrEmpty(found))
                {
                    return found;
                }
            }
        }

        // No csproj pairing found anywhere — fall back to an appsettings.json at this level.
        return appsettingsHere?.Path;
    }

    private async Task<string?> ReadFirstExistingFileAsync(
        string token,
        string owner,
        string repo,
        IReadOnlyList<string> paths,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            var content = await _gitHubService.GetFileContentAsync(token, owner, repo, path, gitRef, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }
        }

        return null;
    }
    public async Task ProvisionAsync(
        Project project,
        DeployTarget serverTarget,
        DatabaseProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.IncludePostgres && !request.IncludeRedis)
        {
            return;
        }

        var provisioning = _provisioningFactory.GetProvisioning(serverTarget.ProviderName);
        if (provisioning is null)
        {
            return;
        }

        var serverConfig = DeployTargetConfig.Parse(serverTarget.ConfigJson);
        if (serverConfig.IsDatabaseTarget)
        {
            return;
        }

        var token = await _tokens.GetTokenAsync(serverTarget.Credential, cancellationToken);
        var credentials = new ProviderCredentials(token);

        ProvisionedDatabaseService? postgres = null;
        ProvisionedDatabaseService? redis = null;

        if (request.IncludePostgres)
        {
            postgres = await provisioning.EnsurePostgresAsync(
                credentials,
                serverTarget.ProviderProjectId,
                request.PostgresDatabaseName,
                cancellationToken);
        }

        if (request.IncludeRedis)
        {
            redis = await provisioning.EnsureRedisAsync(
                credentials,
                serverTarget.ProviderProjectId,
                cancellationToken);
        }

        _logger.LogInformation(
            "DB-PROVISION: provider {Provider} project {ProjectId} ensured postgres={Pg} redis={Redis}",
            serverTarget.ProviderName, serverTarget.ProviderProjectId,
            postgres is null ? "<null>" : postgres.ServiceId, redis is null ? "<null>" : redis.ServiceId);

        var links = string.Equals(serverTarget.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase)
            ? BuildCoolifyVariableLinks(postgres, redis, request.ConnectionStringKeys)
            : BuildVariableLinks(postgres?.ServiceName, redis?.ServiceName);
        _logger.LogInformation("DB-PROVISION: built {Count} variable links: [{Keys}]",
            links.Count, string.Join(",", links.Select(l => l.Key)));
        if (links.Count > 0)
        {
            await provisioning.LinkDatabaseVariablesAsync(
                credentials,
                serverTarget.ProviderProjectId,
                links,
                cancellationToken);
        }

        var railwayProjectId = postgres?.ProjectId ?? redis?.ProjectId;
        if (!string.IsNullOrWhiteSpace(railwayProjectId))
        {
            serverConfig.RailwayProjectId = railwayProjectId;
        }

        serverConfig.IncludePostgres = request.IncludePostgres || serverConfig.IncludePostgres;
        serverConfig.IncludeRedis = request.IncludeRedis || serverConfig.IncludeRedis;
        var serverConfigJson = serverConfig.ToJson();
        var trackedServerTarget = project.DeployTargets.FirstOrDefault(t => t.Id == serverTarget.Id);
        if (trackedServerTarget is not null)
        {
            trackedServerTarget.ConfigJson = serverConfigJson;
        }
        else
        {
            serverTarget.ConfigJson = serverConfigJson;
        }

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE deploy_targets SET "ConfigJson" = {serverConfigJson} WHERE "Id" = {serverTarget.Id}""",
            cancellationToken);

        await UpsertDatabaseDeployTargetAsync(project, serverTarget, postgres, "postgres", cancellationToken);
        await UpsertDatabaseDeployTargetAsync(project, serverTarget, redis, "redis", cancellationToken);

        DetachAllDeployTargetChanges();
    }

    internal static IReadOnlyList<DatabaseVariableLink> BuildCoolifyVariableLinks(
        ProvisionedDatabaseService? postgres,
        ProvisionedDatabaseService? redis,
        IReadOnlyList<string>? detectedConnectionStringKeys = null)
    {
        var links = new List<DatabaseVariableLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string key, string value)
        {
            if (seen.Add(key))
            {
                links.Add(new DatabaseVariableLink(key, value));
            }
        }

        if (postgres is not null && !string.IsNullOrWhiteSpace(postgres.ServiceId))
        {
            Add("DATABASE_URL", postgres.ServiceId);
            Add("ConnectionStrings__Default", postgres.ServiceId);
            Add("ConnectionStrings__DefaultConnection", postgres.ServiceId);
        }

        if (redis is not null && !string.IsNullOrWhiteSpace(redis.ServiceId))
        {
            Add("ConnectionStrings__Redis", redis.ServiceId);
            Add("REDIS_URL", redis.ServiceId);
        }

        // Also set the ConnectionStrings key the app actually reads (from appsettings). Without
        // this, an app that names its connection "Postgres" — not "Default" — never sees the
        // provisioned database. Redis-named keys point at Redis; everything else at Postgres.
        foreach (var key in detectedConnectionStringKeys ?? [])
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var isRedisKey = key.Contains("redis", StringComparison.OrdinalIgnoreCase);
            var service = isRedisKey ? redis : postgres;
            if (service is not null && !string.IsNullOrWhiteSpace(service.ServiceId))
            {
                Add($"ConnectionStrings__{key.Trim()}", service.ServiceId);
            }
        }

        return links;
    }

    internal static IReadOnlyList<DatabaseVariableLink> BuildVariableLinks(
        string? postgresServiceName,
        string? redisServiceName)
    {
        var links = new List<DatabaseVariableLink>();
        if (!string.IsNullOrWhiteSpace(postgresServiceName))
        {
            var postgresHostReference =
                $"Host=${{{{{postgresServiceName}.RAILWAY_PRIVATE_DOMAIN}}}};Port=5432;Username=${{{{{postgresServiceName}.POSTGRES_USER}}}};Password=${{{{{postgresServiceName}.POSTGRES_PASSWORD}}}}";
            var defaultDatabaseReference =
                $"{postgresHostReference};Database=${{{{{postgresServiceName}.POSTGRES_DB}}}}";
            links.Add(new DatabaseVariableLink(
                "ConnectionStrings__Default",
                defaultDatabaseReference));
            links.Add(new DatabaseVariableLink(
                "ConnectionStrings__DefaultConnection",
                defaultDatabaseReference));
            // AdminConnection / TenantTemplate / TestConnection (the last hardcoded to
            // Database=idaara_test) were app-specific to a multi-tenant project and were being
            // written into every deployment. A generic app gets only the default connection.
        }

        if (!string.IsNullOrWhiteSpace(redisServiceName))
        {
            links.Add(new DatabaseVariableLink(
                "ConnectionStrings__Redis",
                $"${{{{{redisServiceName}.REDIS_URL}}}}"));
        }

        return links;
    }

    private async Task UpsertDatabaseDeployTargetAsync(
        Project project,
        DeployTarget serverTarget,
        ProvisionedDatabaseService? database,
        string engine,
        CancellationToken cancellationToken)
    {
        if (database is null)
        {
            return;
        }

        var configJson = DeployTargetConfig.FromDatabaseService(
            engine,
            database.ProjectId,
            database.ServiceName).ToJson();
        var providerProjectId = $"{database.ServiceId}|{database.EnvironmentId}";

        var existingId = await FindDatabaseDeployTargetIdAsync(project.Id, engine, cancellationToken);
        if (existingId is not null)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE deploy_targets
                SET "ConfigJson" = {configJson}, "ProviderProjectId" = {providerProjectId}
                WHERE "Id" = {existingId}
                """,
                cancellationToken);

            UpdateInMemoryDatabaseTarget(project, existingId.Value, providerProjectId, configJson);
            return;
        }

        var newId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO deploy_targets ("Id", "ProjectId", "ProviderName", "CredentialId", "ProviderProjectId", "ConfigJson", "CreatedAt")
            VALUES ({newId}, {project.Id}, {serverTarget.ProviderName}, {serverTarget.CredentialId}, {providerProjectId}, {configJson}, {createdAt})
            """,
            cancellationToken);

        project.DeployTargets.Add(new DeployTarget
        {
            Id = newId,
            ProjectId = project.Id,
            ProviderName = serverTarget.ProviderName,
            CredentialId = serverTarget.CredentialId,
            ProviderProjectId = providerProjectId,
            ConfigJson = configJson,
            CreatedAt = createdAt
        });
    }

    private static void UpdateInMemoryDatabaseTarget(
        Project project,
        Guid deployTargetId,
        string providerProjectId,
        string configJson)
    {
        var existing = project.DeployTargets.FirstOrDefault(t => t.Id == deployTargetId);
        if (existing is null)
        {
            return;
        }

        existing.ProviderProjectId = providerProjectId;
        existing.ConfigJson = configJson;
    }

    private void DetachAllDeployTargetChanges()
    {
        foreach (var entry in _db.ChangeTracker.Entries<DeployTarget>().ToList())
        {
            entry.State = EntityState.Unchanged;
        }
    }

    private async Task<Guid?> FindDatabaseDeployTargetIdAsync(
        Guid projectId,
        string engine,
        CancellationToken cancellationToken)
    {
        var deployTargets = await _db.DeployTargets
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        foreach (var deployTarget in deployTargets)
        {
            var config = DeployTargetConfig.Parse(deployTarget.ConfigJson);
            if (config.IsDatabaseTarget &&
                string.Equals(config.DatabaseEngine, engine, StringComparison.OrdinalIgnoreCase))
            {
                return deployTarget.Id;
            }
        }

        return null;
    }

    public async Task RemoveDatabaseServiceAsync(
        Project project,
        DeployTarget databaseTarget,
        CancellationToken cancellationToken)
    {
        var config = DeployTargetConfig.Parse(databaseTarget.ConfigJson);
        if (!config.IsDatabaseTarget)
        {
            throw new InvalidOperationException("Only database services can be removed through this method.");
        }

        await TeardownDatabaseServiceOnProviderAsync(project, databaseTarget, cancellationToken);

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM deploy_targets WHERE "Id" = {databaseTarget.Id}""",
            cancellationToken);

        project.DeployTargets.Remove(databaseTarget);
        DetachAllDeployTargetChanges();
    }

    public async Task TeardownDatabaseServiceOnProviderAsync(
        Project project,
        DeployTarget databaseTarget,
        CancellationToken cancellationToken)
    {
        var config = DeployTargetConfig.Parse(databaseTarget.ConfigJson);
        if (!config.IsDatabaseTarget)
        {
            throw new InvalidOperationException("Only database services can be removed through this method.");
        }

        var serverTarget = project.DeployTargets.FirstOrDefault(t =>
            !DeployTargetConfig.Parse(t.ConfigJson).IsDatabaseTarget &&
            (string.Equals(t.ProviderName, ProviderNameValues.Railway, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t.ProviderName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase)));

        var provisioning = _provisioningFactory.GetProvisioning(databaseTarget.ProviderName);
        var token = await _tokens.GetTokenAsync(databaseTarget.Credential, cancellationToken);
        var credentials = new ProviderCredentials(token);

        if (provisioning is not null &&
            !string.IsNullOrWhiteSpace(databaseTarget.ProviderProjectId))
        {
            await provisioning.DeleteDatabaseAsync(
                credentials,
                databaseTarget.ProviderProjectId,
                cancellationToken);
        }
        else
        {
            var serviceOperations = _serviceOperationsFactory.GetServiceOperations(databaseTarget.ProviderName);
            if (serviceOperations is not null &&
                !string.IsNullOrWhiteSpace(databaseTarget.ProviderProjectId))
            {
                await serviceOperations.DeleteServiceAsync(
                    credentials,
                    databaseTarget.ProviderProjectId,
                    cancellationToken);
            }
        }

        if (serverTarget is not null)
        {
            var management = _managementFactory.GetManagement(serverTarget.ProviderName);
            var linkKey = config.DatabaseEngine switch
            {
                "postgres" => "ConnectionStrings__Default",
                "redis" => "ConnectionStrings__Redis",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(linkKey) &&
                !string.IsNullOrWhiteSpace(serverTarget.ProviderProjectId))
            {
                try
                {
                    await management.DeleteEnvVarAsync(
                        credentials,
                        serverTarget.ProviderProjectId,
                        linkKey,
                        cancellationToken);
                }
                catch (DeployAI.Core.Exceptions.DeployAIException)
                {
                    // Variable may already be gone.
                }

                if (string.Equals(config.DatabaseEngine, "redis", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await management.DeleteEnvVarAsync(
                            credentials,
                            serverTarget.ProviderProjectId,
                            "REDIS_URL",
                            cancellationToken);
                    }
                    catch (DeployAI.Core.Exceptions.DeployAIException)
                    {
                        // Variable may already be gone.
                    }
                }
            }

            var serverConfig = DeployTargetConfig.Parse(serverTarget.ConfigJson);
            if (string.Equals(config.DatabaseEngine, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                serverConfig.IncludePostgres = false;
            }

            if (string.Equals(config.DatabaseEngine, "redis", StringComparison.OrdinalIgnoreCase))
            {
                serverConfig.IncludeRedis = false;
            }

            var serverConfigJson = serverConfig.ToJson();
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE deploy_targets SET "ConfigJson" = {serverConfigJson} WHERE "Id" = {serverTarget.Id}""",
                cancellationToken);

            var trackedServer = project.DeployTargets.FirstOrDefault(t => t.Id == serverTarget.Id);
            if (trackedServer is not null)
            {
                trackedServer.ConfigJson = serverConfigJson;
            }
        }
    }
}
