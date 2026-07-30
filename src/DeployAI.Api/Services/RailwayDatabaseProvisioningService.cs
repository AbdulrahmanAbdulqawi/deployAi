using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeployAI.Api.Services;

/// <summary>
/// Database (Postgres/Redis) provisioning for a server deploy target - despite the "Railway" name,
/// this dispatches through <see cref="IProviderDatabaseProvisioningFactory"/> by the server target's
/// actual provider, so it works for Coolify server targets too, not just Railway.
/// </summary>
public interface IRailwayDatabaseProvisioningService
{
    /// <summary>Detects which databases a server's repo needs (from docker-compose/appsettings/Prisma).</summary>
    /// <remarks>
    /// A profile whose <see cref="DatabaseRequirementProfile.IsInconclusive"/> is set means the
    /// repository could not be read, not that the app needs nothing.
    /// </remarks>
    Task<DatabaseRequirementProfile> DetectRequirementsAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken);

    /// <summary>Provisions exactly the requested database engines and links their connection strings to the server target.</summary>
    Task ProvisionAsync(
        Project project,
        DeployTarget serverTarget,
        DatabaseProvisioningRequest request,
        CancellationToken cancellationToken);

    /// <summary>Detects requirements from the repo and provisions exactly those databases automatically.</summary>
    /// <returns>What the deploy log should say, or null when there is nothing worth saying.</returns>
    Task<string?> EnsureFromRepoAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken);

    /// <summary>Tears down a database on its provider and removes its DeployAI record.</summary>
    Task RemoveDatabaseServiceAsync(
        Project project,
        DeployTarget databaseTarget,
        CancellationToken cancellationToken);

    /// <summary>Tears down a database on its provider only, without touching the DeployAI record (used mid-project-teardown, where the record is removed separately).</summary>
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
    private readonly IRepositoryLayoutResolver _layoutResolver;
    private readonly IRepositoryReader _reader;
    private readonly IEncryptionService _encryption;
    private readonly IDatabaseRequirementDetector _databaseRequirementDetector;
    private readonly ILogger<RailwayDatabaseProvisioningService> _logger;

    public RailwayDatabaseProvisioningService(
        DeployAIDbContext db,
        IProviderDatabaseProvisioningFactory provisioningFactory,
        IProviderManagementFactory managementFactory,
        IProviderServiceOperationsFactory serviceOperationsFactory,
        IProviderCredentialTokenService tokens,
        IRepositoryLayoutResolver layoutResolver,
        IRepositoryReader reader,
        IEncryptionService encryption,
        IDatabaseRequirementDetector databaseRequirementDetector,
        ILogger<RailwayDatabaseProvisioningService> logger)
    {
        _db = db;
        _provisioningFactory = provisioningFactory;
        _managementFactory = managementFactory;
        _serviceOperationsFactory = serviceOperationsFactory;
        _tokens = tokens;
        _layoutResolver = layoutResolver;
        _reader = reader;
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

    public async Task<string?> EnsureFromRepoAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken)
    {
        if (_provisioningFactory.GetProvisioning(serverTarget.ProviderName) is null)
        {
            _logger.LogWarning("DB-PROVISION: no provisioning registered for provider {Provider}; skipping.", serverTarget.ProviderName);
            return null;
        }

        var profile = await DetectRequirementsInternalAsync(project, serverTarget, branch, cancellationToken);
        _logger.LogInformation(
            "DB-PROVISION: detection for target {TargetId} on branch {Branch}: postgres={Pg} redis={Redis} keys=[{Keys}] pgName={PgName} inconclusive={Inconclusive}",
            serverTarget.Id, branch, profile.RequiresPostgres, profile.RequiresRedis,
            string.Join(",", profile.ConnectionStringKeys ?? []), profile.PostgresDatabaseName,
            profile.IsInconclusive);

        // A scan that could not read the repository is not a scan that found no database. Deploying
        // on the first is fine; deploying silently on the second hands the app an empty connection
        // string, and the symptom arrives later as a 500 from a route nobody connected to this.
        if (profile.IsInconclusive)
        {
            return "Could not read this app's files, so whether it needs a database is unknown. "
                + "If it does, no database was provisioned and it will fail at its first query.";
        }

        if (!profile.RequiresPostgres && !profile.RequiresRedis)
        {
            return null;
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

        return null;
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
        // A compose target's own RootDirectory/ServiceDirectory is the website's — the folder that
        // faces the browser, e.g. "client" — because that is the half the persisted role describes.
        // The server code compose deploys alongside it lives at ComposeServerDirectory, recorded
        // separately for exactly this reason. Reading the website's directory here sent detection
        // looking for appsettings.json under client/, found nothing, and reported no database
        // requirement for an app that fails on line one of Program.cs without one.
        var serverPath = (serverConfig.IsComposeTarget
                ? serverConfig.ComposeServerDirectory ?? string.Empty
                : serverConfig.ServiceDirectory ?? serverConfig.RootDirectory ?? string.Empty)
            .Trim()
            .Trim('/');

        // The shared resolver, not this service's own descent. It used to walk two levels looking
        // for an appsettings.json next to a .csproj — correct, and the third independent copy of
        // the same idea. Everything it read now comes through the layout's search path instead, so
        // a repository shape this service has never seen is one resolver's problem, not four.
        var layout = await _layoutResolver.ResolveAsync(
            gitHubToken, parts[0], parts[1], branch, serverPath, cancellationToken);

        if (layout.IsInconclusive)
        {
            _logger.LogWarning(
                "DB-PROVISION: could not read {Repo}@{Branch} under '{Path}'; database requirements are unknown.",
                project.GitHubRepoFullName, branch, serverPath);
            return new DatabaseRequirementProfile(false, false, [], IsInconclusive: true);
        }

        var dockerCompose =
            await _reader.FindAsync(gitHubToken, parts[0], parts[1], branch, layout, "docker-compose.yml", cancellationToken)
            ?? await _reader.FindAsync(gitHubToken, parts[0], parts[1], branch, layout, "docker-compose.yaml", cancellationToken);
        var appsettings = await _reader.FindAsync(
            gitHubToken, parts[0], parts[1], branch, layout, "appsettings.json", cancellationToken);

        // Read here for the first time: classification already looked at a Prisma schema, but the
        // deploy path did not, so a Node app whose only evidence is prisma/schema.prisma was told at
        // the wizard that it needs Postgres and then deployed without one.
        var prismaSchema = await _reader.FindAsync(
            gitHubToken, parts[0], parts[1], branch, layout, "prisma/schema.prisma", cancellationToken);

        return _databaseRequirementDetector.Detect(
            dockerCompose?.Content, appsettings?.Content, prismaSchema?.Content);
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
