using DeployAI.Api.Services;

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
}
