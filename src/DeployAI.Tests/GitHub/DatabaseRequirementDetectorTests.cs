using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.GitHub;

public class DatabaseRequirementDetectorTests
{
    private readonly DatabaseRequirementDetector _detector = new();

    [Fact]
    public void Detect_FindsPostgresAndRedis_FromIdaaraLikeComposeAndAppsettings()
    {
        const string compose = """
            services:
              postgres:
                image: postgres:16-alpine
              redis:
                image: redis:7-alpine
            """;

        const string appsettings = """
            {
              "ConnectionStrings": {
                "DefaultConnection": "Host=localhost;Port=5432;Database=idaara;Username=idaara;Password=idaara",
                "Redis": "localhost:6379"
              }
            }
            """;

        var profile = _detector.Detect(compose, appsettings);

        Assert.True(profile.RequiresPostgres);
        Assert.True(profile.RequiresRedis);
        Assert.Equal("idaara", profile.PostgresDatabaseName);
        Assert.Contains("DefaultConnection", profile.ConnectionStringKeys);
        Assert.Contains("Redis", profile.ConnectionStringKeys);
    }

    [Fact]
    // yemenConnect's appsettings.json carries a UTF-8 BOM and names its connection strings
    // "Postgres"/"Redis" rather than "DefaultConnection". The BOM made JsonDocument.Parse throw,
    // silently reporting "no database" — so its Postgres + Redis needs went undetected.
    public void Detect_FindsPostgresAndRedis_FromBomPrefixedAppsettingsWithCustomKeys()
    {
        const string appsettings = "﻿" + """
            {
              "ConnectionStrings": {
                "Postgres": "Host=localhost;Port=5432;Database=yemenhub;Username=postgres;Password=postgres",
                "Redis": "localhost:6380"
              }
            }
            """;

        var profile = _detector.Detect(dockerComposeContent: null, appsettings);

        Assert.True(profile.RequiresPostgres);
        Assert.True(profile.RequiresRedis);
        Assert.Equal("yemenhub", profile.PostgresDatabaseName);
        Assert.Contains("Postgres", profile.ConnectionStringKeys);
    }

    [Fact]
    public void Detect_FindsPostgres_FromPrismaSchema()
    {
        const string prisma = """
            datasource db {
              provider = "postgresql"
              url      = env("DATABASE_URL")
            }
            """;

        var profile = _detector.Detect(null, null, prisma);

        Assert.True(profile.RequiresPostgres);
        Assert.False(profile.RequiresRedis);
    }

    [Fact]
    public void DetectPostgresInCompose_MatchesPostgresImage()
    {
        const string compose = """
            services:
              db:
                image: 'postgres:16'
            """;

        var profile = _detector.Detect(compose, null);

        Assert.True(profile.RequiresPostgres);
        Assert.False(profile.RequiresRedis);
    }

    [Fact]
    public void Detect_ExtractsPostgresDatabaseNameFromCompose_WhenAppsettingsMissing()
    {
        const string compose = """
            services:
              postgres:
                image: postgres:16
                environment:
                  POSTGRES_USER: deployai
                  POSTGRES_PASSWORD: deployai
                  POSTGRES_DB: deployai
            """;

        var profile = _detector.Detect(compose, null);

        Assert.True(profile.RequiresPostgres);
        Assert.Equal("deployai", profile.PostgresDatabaseName);
    }

    [Fact]
    public void DetectFromAppsettings_RequiresPostgresForDefaultConnection()
    {
        const string appsettings = """
            {
              "ConnectionStrings": {
                "DefaultConnection": "Host=db;Database=app"
              }
            }
            """;

        var profile = _detector.Detect(null, appsettings);

        Assert.True(profile.RequiresPostgres);
        Assert.False(profile.RequiresRedis);
        Assert.Single(profile.ConnectionStringKeys);
    }

    [Fact]
    public void DetectFromAppsettings_RequiresRedisWhenKeyPresent()
    {
        const string appsettings = """
            {
              "ConnectionStrings": {
                "Redis": ""
              }
            }
            """;

        var profile = _detector.Detect(null, appsettings);

        Assert.True(profile.RequiresRedis);
    }

    [Fact]
    public void ExtractPostgresDatabaseName_PrefersDefaultConnection()
    {
        const string appsettings = """
            {
              "ConnectionStrings": {
                "AdminConnection": "Host=db;Database=admin",
                "DefaultConnection": "Host=db;Database=idaara;Username=idaara;Password=idaara"
              }
            }
            """;

        var profile = _detector.Detect(null, appsettings);

        Assert.Equal("idaara", profile.PostgresDatabaseName);
    }

    [Fact]
    public void Detect_ExtractsDatabaseNameFromPostgresUrl()
    {
        const string appsettings = """
            {
              "ConnectionStrings": {
                "DefaultConnection": "postgresql://user:pass@host:5432/myapp"
              }
            }
            """;

        var profile = _detector.Detect(null, appsettings);

        Assert.True(profile.RequiresPostgres);
        Assert.Equal("myapp", profile.PostgresDatabaseName);
    }

    [Fact]
    // Mirqab's appsettings.json has no ConnectionStrings section at all — Program.cs reads
    // GetConnectionString("Postgres") directly in code. But its docker-compose.yml, read for the
    // image check, already spells out the exact key it needs: ConnectionStrings__Postgres, the
    // standard double-underscore env-var form of the same config path. Nothing read it for the key
    // name, only for the postgres image — so provisioning succeeded and wired only the generic
    // defaults (ConnectionStrings__Default, DATABASE_URL), never the one key the app actually reads,
    // and the container kept crash-looping on "ConnectionStrings:Postgres is not configured".
    public void Detect_ExtractsConnectionStringKey_FromComposeEnvironmentVariable_WhenAppsettingsDeclaresNone()
    {
        const string compose = """
            services:
              postgres:
                image: postgres:17-alpine
                environment:
                  POSTGRES_DB: mirqab
              api:
                environment:
                  ConnectionStrings__Postgres: "Host=postgres;Port=5432;Database=mirqab;Username=mirqab;Password=mirqab_dev"
            """;

        const string appsettings = """
            {
              "Logging": { "LogLevel": { "Default": "Information" } },
              "AllowedHosts": "*"
            }
            """;

        var profile = _detector.Detect(compose, appsettings);

        Assert.True(profile.RequiresPostgres);
        Assert.Equal("mirqab", profile.PostgresDatabaseName);
        Assert.Contains("Postgres", profile.ConnectionStringKeys);
    }
}
