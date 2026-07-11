using DeployAI.Api.Services;
using DeployAI.Core.Providers;

namespace DeployAI.Tests.Services;

public class RailwayDatabaseProvisioningServiceTests
{
    [Fact]
    public void BuildVariableLinks_UsesRailwayReferenceSyntax()
    {
        var links = RailwayDatabaseProvisioningService.BuildVariableLinks("Postgres", "Redis");

        Assert.Equal(6, links.Count);
        Assert.Equal("ConnectionStrings__Default", links[0].Key);
        Assert.Contains("Postgres.POSTGRES_DB", links[0].ReferenceValue, StringComparison.Ordinal);
        Assert.Equal("ConnectionStrings__DefaultConnection", links[1].Key);
        Assert.Contains("Postgres.POSTGRES_DB", links[1].ReferenceValue, StringComparison.Ordinal);
        Assert.Equal("ConnectionStrings__AdminConnection", links[2].Key);
        Assert.Contains("Database=postgres", links[2].ReferenceValue, StringComparison.Ordinal);
        Assert.Equal("ConnectionStrings__TenantTemplate", links[3].Key);
        Assert.Equal("ConnectionStrings__TestConnection", links[4].Key);
        Assert.Contains("Database=idaara_test", links[4].ReferenceValue, StringComparison.Ordinal);
        Assert.Equal("ConnectionStrings__Redis", links[5].Key);
        Assert.Equal("${{Redis.REDIS_URL}}", links[5].ReferenceValue);
    }

    [Fact]
    public void BuildVariableLinks_SkipsMissingServices()
    {
        var links = RailwayDatabaseProvisioningService.BuildVariableLinks("Postgres", null);

        Assert.Equal(5, links.Count);
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
}
