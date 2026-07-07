using DeployAI.Providers.Railway;
using DeployAI.Providers.Railway.GraphQL;

namespace DeployAI.Tests.Providers;

public class RailwayProviderNetworkingTests
{
    [Fact]
    public void ResolvePublicServiceUrl_PrefersDeploymentUrl()
    {
        var url = RailwayProvider.ResolvePublicServiceUrl(
            "https://deploy.example.com",
            [new RailwayProvider.ServiceDomainSnapshot("api.up.railway.app", ServiceDomainSyncStatus.Active)]);

        Assert.Equal("https://deploy.example.com", url);
    }

    [Fact]
    public void ResolvePublicServiceUrl_FallsBackToServiceDomain()
    {
        var url = RailwayProvider.ResolvePublicServiceUrl(
            null,
            [new RailwayProvider.ServiceDomainSnapshot("api-production.up.railway.app", ServiceDomainSyncStatus.Active)]);

        Assert.Equal("https://api-production.up.railway.app", url);
    }

    [Fact]
    public void ResolvePublicServiceUrl_IgnoresDeletedDomains()
    {
        var url = RailwayProvider.ResolvePublicServiceUrl(
            null,
            [new RailwayProvider.ServiceDomainSnapshot("old.up.railway.app", ServiceDomainSyncStatus.Deleted)]);

        Assert.Null(url);
    }
}
