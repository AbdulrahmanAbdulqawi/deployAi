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
}
