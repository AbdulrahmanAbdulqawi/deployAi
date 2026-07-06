using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Data.Entities;
using Npgsql;

namespace DeployAI.Api.Services;

public interface IDataServiceInspectionService
{
    Task<object> GetDataServiceInfoAsync(
        DeployTarget target,
        ProviderCredentials credentials,
        CancellationToken cancellationToken);
}

public sealed class DataServiceInspectionService : IDataServiceInspectionService
{
    private readonly IProviderDataServiceInspectionFactory _inspectionFactory;

    public DataServiceInspectionService(IProviderDataServiceInspectionFactory inspectionFactory)
    {
        _inspectionFactory = inspectionFactory;
    }

    public async Task<object> GetDataServiceInfoAsync(
        DeployTarget target,
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        var config = DeployTargetConfig.Parse(target.ConfigJson);
        if (!config.IsDatabaseTarget)
        {
            throw new DeployAIException("invalid_target", "Only data services support database inspection.");
        }

        var inspection = _inspectionFactory.GetInspection(target.ProviderName)
            ?? throw new DeployAIException("unsupported_provider", "Data service inspection is not available for this provider.");

        var engine = config.DatabaseEngine ?? "unknown";
        var metadata = await inspection.GetMetadataAsync(
            credentials,
            target.ProviderProjectId,
            engine,
            cancellationToken);

        var linkedKeys = GetLinkedConnectionKeys(engine);
        var railwayUrl = BuildRailwayUrl(metadata);

        if (!string.Equals(engine, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                engine,
                metadata = MapMetadata(metadata),
                linkedConnectionKeys = linkedKeys,
                connectionSummary = BuildRedisConnectionSummary(metadata),
                railwayUrl
            };
        }

        var inspectionResult = await InspectPostgresAsync(inspection, credentials, target.ProviderProjectId, cancellationToken);
        var connectionDetails = await inspection.GetPostgresConnectionDetailsAsync(
            credentials,
            target.ProviderProjectId,
            cancellationToken);

        return new
        {
            engine,
            metadata = MapMetadata(metadata),
            linkedConnectionKeys = linkedKeys,
            connectionSummary = BuildPostgresConnectionSummary(connectionDetails),
            railwayUrl,
            inspection = new
            {
                connected = inspectionResult.Connected,
                message = inspectionResult.Message,
                tables = inspectionResult.Tables.Select(table => new { name = table.Name, rowCount = table.RowCount }),
                migrationsApplied = inspectionResult.MigrationsApplied
            }
        };
    }

    private static async Task<PostgresDatabaseInspection> InspectPostgresAsync(
        IProviderDataServiceInspection inspection,
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var connectionDetails = await inspection.GetPostgresConnectionDetailsAsync(
            credentials,
            providerProjectId,
            cancellationToken);

        if (connectionDetails is null)
        {
            return new PostgresDatabaseInspection(
                false,
                "PostgreSQL connection settings are not available yet.",
                [],
                []);
        }

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = connectionDetails.Host,
            Port = connectionDetails.Port,
            Database = connectionDetails.Database,
            Username = connectionDetails.Username,
            Password = connectionDetails.Password,
            Timeout = 5,
            CommandTimeout = 10
        }.ConnectionString;

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var tables = await ReadTablesAsync(connection, cancellationToken);
            var migrations = await ReadMigrationsAsync(connection, cancellationToken);

            return new PostgresDatabaseInspection(
                true,
                null,
                tables,
                migrations);
        }
        catch (Exception ex)
        {
            return new PostgresDatabaseInspection(
                false,
                ex.Message,
                [],
                []);
        }
    }

    private static async Task<IReadOnlyList<DatabaseTableInfo>> ReadTablesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT relname AS table_name, COALESCE(n_live_tup, 0) AS row_count
            FROM pg_stat_user_tables
            ORDER BY relname
            """;

        var tables = new List<DatabaseTableInfo>();
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new DatabaseTableInfo(
                reader.GetString(0),
                reader.GetInt64(1)));
        }

        return tables;
    }

    private static async Task<IReadOnlyList<string>> ReadMigrationsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string existsQuery = """
            SELECT EXISTS (
              SELECT 1
              FROM information_schema.tables
              WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory'
            )
            """;

        await using var existsCommand = new NpgsqlCommand(existsQuery, connection);
        var exists = (bool?)await existsCommand.ExecuteScalarAsync(cancellationToken) ?? false;
        if (!exists)
        {
            return [];
        }

        const string migrationsQuery = """
            SELECT "MigrationId"
            FROM "__EFMigrationsHistory"
            ORDER BY "MigrationId"
            """;

        var migrations = new List<string>();
        await using var migrationsCommand = new NpgsqlCommand(migrationsQuery, connection);
        await using var reader = await migrationsCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            migrations.Add(reader.GetString(0));
        }

        return migrations;
    }

    private static object MapMetadata(DataServiceMetadata metadata) => new
    {
        databaseName = metadata.DatabaseName,
        host = metadata.Host,
        port = metadata.Port,
        volumeMountPath = metadata.VolumeMountPath,
        railwayServiceId = metadata.RailwayServiceId,
        railwayProjectId = metadata.RailwayProjectId,
        railwayEnvironmentId = metadata.RailwayEnvironmentId
    };

    private static string? BuildRailwayUrl(DataServiceMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.RailwayProjectId) ||
            string.IsNullOrWhiteSpace(metadata.RailwayServiceId))
        {
            return null;
        }

        return $"https://railway.com/project/{metadata.RailwayProjectId}/service/{metadata.RailwayServiceId}";
    }

    private static string? BuildPostgresConnectionSummary(PostgresConnectionDetails? details)
    {
        if (details is null)
        {
            return null;
        }

        return $"postgresql://{details.Username}:***@{details.Host}:{details.Port}/{details.Database}";
    }

    private static string? BuildRedisConnectionSummary(DataServiceMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.Host) || metadata.Port is null)
        {
            return null;
        }

        return $"redis://{metadata.Host}:{metadata.Port}";
    }

    private static IReadOnlyList<string> GetLinkedConnectionKeys(string engine) =>
        engine switch
        {
            "postgres" => ["ConnectionStrings__Default", "ConnectionStrings__DefaultConnection"],
            "redis" => ["ConnectionStrings__Redis"],
            _ => []
        };
}
