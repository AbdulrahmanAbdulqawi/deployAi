using DeployAI.Api.Services;
using DeployAI.Core.Providers;

namespace DeployAI.Tests.Services;

public class RailwayDatabaseProvisioningServiceTests
{
    [Fact]
    public void BuildVariableLinks_UsesRailwayReferenceSyntax()
    {
        var links = RailwayDatabaseProvisioningService.BuildVariableLinks("Postgres", "Redis");

        // Only the generic default connection strings — the app-specific Admin/Tenant/Test
        // links (idaara multi-tenant) no longer leak into every deployment.
        Assert.Equal(3, links.Count);
        Assert.Equal("ConnectionStrings__Default", links[0].Key);
        Assert.Contains("Postgres.POSTGRES_DB", links[0].ReferenceValue, StringComparison.Ordinal);
        Assert.Equal("ConnectionStrings__DefaultConnection", links[1].Key);
        Assert.Contains("Postgres.POSTGRES_DB", links[1].ReferenceValue, StringComparison.Ordinal);
        Assert.Equal("ConnectionStrings__Redis", links[2].Key);
        Assert.Equal("${{Redis.REDIS_URL}}", links[2].ReferenceValue);
        Assert.DoesNotContain(links, link => link.ReferenceValue.Contains("idaara_test", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildVariableLinks_SkipsMissingServices()
    {
        var links = RailwayDatabaseProvisioningService.BuildVariableLinks("Postgres", null);

        Assert.Equal(2, links.Count);
        Assert.Equal("ConnectionStrings__Default", links[0].Key);
        Assert.Contains("Postgres.RAILWAY_PRIVATE_DOMAIN", links[0].ReferenceValue, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCoolifyVariableLinks_IncludesPostgresAndRedisDatabaseIds()
    {
        var links = RailwayDatabaseProvisioningService.BuildCoolifyVariableLinks(
            new ProvisionedDatabaseService("db-pg", "my-api-postgres", "proj-1", "env-1"),
            new ProvisionedDatabaseService("db-redis", "my-api-redis", "proj-1", "env-1"));

        Assert.Equal(5, links.Count);
        Assert.Contains(links, link => link.Key == "DATABASE_URL" && link.ReferenceValue == "db-pg");
        Assert.Contains(links, link => link.Key == "ConnectionStrings__Redis" && link.ReferenceValue == "db-redis");
        Assert.Contains(links, link => link.Key == "REDIS_URL" && link.ReferenceValue == "db-redis");
    }

    [Fact]
    // yemenConnect reads ConnectionStrings:Postgres and ConnectionStrings:Redis (not the
    // conventional "Default"), so the provisioned databases must be wired to those exact keys or
    // the app never sees them.
    public void BuildCoolifyVariableLinks_WiresDetectedConnectionStringKeys()
    {
        var links = RailwayDatabaseProvisioningService.BuildCoolifyVariableLinks(
            new ProvisionedDatabaseService("db-pg", "yemenhub-postgres", "proj-1", "env-1"),
            new ProvisionedDatabaseService("db-redis", "yemenhub-redis", "proj-1", "env-1"),
            detectedConnectionStringKeys: ["Postgres", "Redis"]);

        // The app's own key gets the Postgres service; the Redis-named key gets Redis.
        Assert.Contains(links, link => link.Key == "ConnectionStrings__Postgres" && link.ReferenceValue == "db-pg");
        Assert.Contains(links, link => link.Key == "ConnectionStrings__Redis" && link.ReferenceValue == "db-redis");
        // Still keyless-safe: no duplicate ConnectionStrings__Redis.
        Assert.Single(links, link => link.Key == "ConnectionStrings__Redis");
    }
}
