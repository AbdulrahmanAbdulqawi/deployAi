using System.Net;
using System.Net.Http.Json;
using DeployAI.Core.Providers;
using DeployAI.Providers.Vercel;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

public class VercelApiSupportRoutingTests
{
    [Fact]
    public void NormalizeExternalOrigin_AddsHttpsAndTrimsSlash()
    {
        Assert.Equal("https://api.example.com", VercelApiSupport.NormalizeExternalOrigin("api.example.com/"));
    }

    [Fact]
    public void ExtractPrimaryProductionAlias_PrefersProvidedAlias()
    {
        var alias = VercelApiSupport.ExtractPrimaryProductionAlias(
            ["deployai-mu.vercel.app", "deployai.vercel.app"],
            "deployai");

        Assert.Equal("deployai-mu.vercel.app", alias);
    }

    [Fact]
    public void ExtractPrimaryProductionAlias_FallsBackToProjectName()
    {
        var alias = VercelApiSupport.ExtractPrimaryProductionAlias([], "deployai");
        Assert.Equal("deployai.vercel.app", alias);
    }

    [Fact]
    public void ExtractPrimaryProductionAlias_PrefersSuffixedProductionOverBareProjectDomain()
    {
        var alias = VercelApiSupport.ExtractPrimaryProductionAlias(
            [
                "idaara-git-main-user-projects.vercel.app",
                "idaara.vercel.app",
                "idaara-livid.vercel.app"
            ],
            "idaara");

        Assert.Equal("idaara-livid.vercel.app", alias);
    }

    [Fact]
    public void ExtractPrimaryProductionAlias_SkipsPreviewGitAliases()
    {
        var alias = VercelApiSupport.ExtractPrimaryProductionAlias(
            ["idaara-git-main-user-projects.vercel.app", "deployai-mu.vercel.app"],
            "idaara");

        Assert.Equal("deployai-mu.vercel.app", alias);
    }

    [Theory]
    [InlineData("idaara-git-main-user-projects.vercel.app", true)]
    [InlineData("idaara-1tlfebzm4-user-7865s-projects.vercel.app", true)]
    [InlineData("portfolio-e9aptsrdq-abdulrahmanabdulqawi76-7865s-projects.vercel.app", true)]
    [InlineData("idaara-livid.vercel.app", false)]
    [InlineData("idaara.vercel.app", false)]
    [InlineData("portfolio-six-jet-40.vercel.app", false)]
    public void IsPreviewAlias_DetectsPreviewHosts(string alias, bool expected)
    {
        Assert.Equal(expected, VercelApiSupport.IsPreviewAlias(alias));
    }

    [Fact]
    public void ExtractPrimaryProductionAlias_UsesVercelWordSuffixedProductionDomain()
    {
        var alias = VercelApiSupport.ExtractPrimaryProductionAlias(
            [
                "portfolio-e9aptsrdq-abdulrahmanabdulqawi76-7865s-projects.vercel.app",
                "portfolio-six-jet-40.vercel.app"
            ],
            "portfolio");

        Assert.Equal("portfolio-six-jet-40.vercel.app", alias);
    }
}

public class VercelProviderRoutingTests
{
    private static VercelProvider CreateProvider(MockHttpMessageHandler handler)
    {
        var client = handler.ToHttpClient();
        client.BaseAddress = new Uri("https://api.vercel.com/");
        return new VercelProvider(client);
    }

    [Fact]
    public async Task EnsureApiProxyRoutesAsync_CreatesRoutesAndPromotes()
    {
        var handler = new MockHttpMessageHandler();
        var routePosts = 0;
        handler.When(HttpMethod.Get, "https://api.vercel.com/v1/projects/prj_1/routes")
            .Respond(HttpStatusCode.OK, "application/json", """{"routes":[]}""");
        handler.When(HttpMethod.Post, "https://api.vercel.com/v1/projects/prj_1/routes")
            .Respond(_ =>
            {
                routePosts++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"route_1"}""", System.Text.Encoding.UTF8, "application/json")
                };
            });
        handler.When(HttpMethod.Get, "https://api.vercel.com/v1/projects/prj_1/routes/versions")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "versions": [
                { "id": "v_staging", "isStaging": true, "createdBy": "user", "lastModified": 1, "s3Key": "key" }
              ]
            }
            """);
        handler.When(HttpMethod.Post, "https://api.vercel.com/v1/projects/prj_1/routes/versions")
            .Respond(HttpStatusCode.OK, "application/json", """{"version":{"id":"v_staging","isLive":true}}""");

        var provider = CreateProvider(handler);
        await ((IWebsiteApiProxySupport)provider).EnsureApiProxyRoutesAsync(
            new ProviderCredentials("token"),
            "prj_1",
            "https://api.example.com",
            CancellationToken.None);

        Assert.Equal(2, routePosts);
    }

    [Fact]
    public async Task ResolvePublicWebsiteUrlAsync_UsesProjectAlias()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/prj_1")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "id": "prj_1",
              "name": "deployai",
              "alias": ["deployai-mu.vercel.app"]
            }
            """);
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/prj_1/domains*")
            .Respond(HttpStatusCode.OK, "application/json", """{"domains":[]}""");
        handler.When(HttpMethod.Get, "https://api.vercel.com/v6/deployments*")
            .Respond(HttpStatusCode.OK, "application/json", """{"deployments":[]}""");

        var provider = CreateProvider(handler);
        var url = await ((IWebsiteApiProxySupport)provider).ResolvePublicWebsiteUrlAsync(
            new ProviderCredentials("token"),
            "prj_1",
            "https://deployai-preview.vercel.app",
            CancellationToken.None);

        Assert.Equal("https://deployai-mu.vercel.app", url);
    }

    [Fact]
    public async Task ResolvePublicWebsiteUrlAsync_UsesProductionDeploymentAlias_WhenProjectAliasEmpty()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/prj_idaara")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "id": "prj_idaara",
              "name": "idaara",
              "alias": []
            }
            """);
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/prj_idaara/domains*")
            .Respond(HttpStatusCode.OK, "application/json", """{"domains":[]}""");
        handler.When(HttpMethod.Get, "https://api.vercel.com/v6/deployments*")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "deployments": [
                {
                  "url": "idaara-abc123.vercel.app",
                  "alias": ["idaara-livid.vercel.app"]
                }
              ]
            }
            """);

        var provider = CreateProvider(handler);
        var url = await ((IWebsiteApiProxySupport)provider).ResolvePublicWebsiteUrlAsync(
            new ProviderCredentials("token"),
            "prj_idaara",
            "https://idaara-fo9p0ajqc-user-projects.vercel.app",
            CancellationToken.None);

        Assert.Equal("https://idaara-livid.vercel.app", url);
    }

    [Fact]
    public async Task GetLatestProductionDeploymentIdAsync_UsesAmpersandTeamQuery_WhenPathAlreadyHasQuery()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/prj_team")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "id": "prj_team",
              "name": "idaara",
              "accountId": "team_pM9mpoCoRkoBA5PX0PTuMVPH"
            }
            """);
        handler.When(HttpMethod.Get,
                "https://api.vercel.com/v6/deployments?projectId=prj_team&target=production&limit=20&teamId=team_pM9mpoCoRkoBA5PX0PTuMVPH")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "deployments": [
                { "uid": "dpl_latest", "readyState": "READY" }
              ]
            }
            """);

        var provider = CreateProvider(handler);
        var deploymentId = await ((IWebsiteApiProxySupport)provider).GetLatestProductionDeploymentIdAsync(
            new ProviderCredentials("token"),
            "prj_team",
            CancellationToken.None);

        Assert.Equal("dpl_latest", deploymentId);
    }

    [Fact]
    public async Task GetLatestProductionDeploymentIdAsync_SkipsDeploymentsThatAreNotReady()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/prj_team")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "id": "prj_team",
              "name": "idaara",
              "accountId": "team_pM9mpoCoRkoBA5PX0PTuMVPH"
            }
            """);
        handler.When(HttpMethod.Get,
                "https://api.vercel.com/v6/deployments?projectId=prj_team&target=production&limit=20&teamId=team_pM9mpoCoRkoBA5PX0PTuMVPH")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "deployments": [
                { "uid": "dpl_building", "readyState": "BUILDING" },
                { "uid": "dpl_ready", "readyState": "READY" }
              ]
            }
            """);

        var provider = CreateProvider(handler);
        var deploymentId = await ((IWebsiteApiProxySupport)provider).GetLatestProductionDeploymentIdAsync(
            new ProviderCredentials("token"),
            "prj_team",
            CancellationToken.None);

        Assert.Equal("dpl_ready", deploymentId);
    }

    [Fact]
    public async Task AssignProductionDomainsToDeploymentAsync_PostsAliasWithTeamQuery()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/prj_team")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "id": "prj_team",
              "name": "idaara",
              "accountId": "team_pM9mpoCoRkoBA5PX0PTuMVPH"
            }
            """);
        handler.When(HttpMethod.Get,
                "https://api.vercel.com/v9/projects/prj_team/domains?production=true&teamId=team_pM9mpoCoRkoBA5PX0PTuMVPH")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "domains": [
                { "name": "idaara-livid.vercel.app" }
              ]
            }
            """);
        handler.When(HttpMethod.Post,
                "https://api.vercel.com/v2/deployments/dpl_latest/aliases?teamId=team_pM9mpoCoRkoBA5PX0PTuMVPH")
            .Respond(HttpStatusCode.OK, "application/json", """{"alias":"idaara-livid.vercel.app"}""");

        var provider = CreateProvider(handler);
        await ((IWebsiteApiProxySupport)provider).AssignProductionDomainsToDeploymentAsync(
            new ProviderCredentials("token"),
            "prj_team",
            "dpl_latest",
            CancellationToken.None);
    }

    [Fact]
    public async Task AssignProductionDomainsToDeploymentAsync_TreatsAlreadyAssociatedConflictAsSuccess()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/prj_team")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "id": "prj_team",
              "name": "idaara",
              "accountId": "team_pM9mpoCoRkoBA5PX0PTuMVPH"
            }
            """);
        handler.When(HttpMethod.Get,
                "https://api.vercel.com/v9/projects/prj_team/domains?production=true&teamId=team_pM9mpoCoRkoBA5PX0PTuMVPH")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "domains": [
                { "name": "idaara-livid.vercel.app" }
              ]
            }
            """);
        handler.When(HttpMethod.Post,
                "https://api.vercel.com/v2/deployments/dpl_latest/aliases?teamId=team_pM9mpoCoRkoBA5PX0PTuMVPH")
            .Respond(HttpStatusCode.Conflict, "application/json", """
            {
              "error": {
                "code": "alias_in_use",
                "message": "The domain you are trying to associate is already associated with this deployment"
              }
            }
            """);

        var provider = CreateProvider(handler);
        await ((IWebsiteApiProxySupport)provider).AssignProductionDomainsToDeploymentAsync(
            new ProviderCredentials("token"),
            "prj_team",
            "dpl_latest",
            CancellationToken.None);
    }

    [Fact]
    public async Task AssignProductionDomainsToDeploymentAsync_TreatsNotReadyDeploymentAsDeferred()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/prj_team")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "id": "prj_team",
              "name": "idaara",
              "accountId": "team_pM9mpoCoRkoBA5PX0PTuMVPH"
            }
            """);
        handler.When(HttpMethod.Get,
                "https://api.vercel.com/v9/projects/prj_team/domains?production=true&teamId=team_pM9mpoCoRkoBA5PX0PTuMVPH")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "domains": [
                { "name": "idaara-kappa.vercel.app" }
              ]
            }
            """);
        handler.When(HttpMethod.Post,
                "https://api.vercel.com/v2/deployments/dpl_building/aliases?teamId=team_pM9mpoCoRkoBA5PX0PTuMVPH")
            .Respond(HttpStatusCode.BadRequest, "application/json", """
            {
              "error": {
                "code": "deployment_not_ready",
                "message": "The deployment `readyState` is not `READY`"
              }
            }
            """);

        var provider = CreateProvider(handler);
        await ((IWebsiteApiProxySupport)provider).AssignProductionDomainsToDeploymentAsync(
            new ProviderCredentials("token"),
            "prj_team",
            "dpl_building",
            CancellationToken.None);
    }
}
