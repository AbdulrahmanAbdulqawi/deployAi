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
        handler.When(HttpMethod.Post, "https://api.vercel.com/v1/projects/prj_1/routes/versions")
            .Respond(HttpStatusCode.OK, "application/json", """{"version":{"id":"v1"}}""");

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

        var provider = CreateProvider(handler);
        var url = await ((IWebsiteApiProxySupport)provider).ResolvePublicWebsiteUrlAsync(
            new ProviderCredentials("token"),
            "prj_1",
            "https://deployai-preview.vercel.app",
            CancellationToken.None);

        Assert.Equal("https://deployai-mu.vercel.app", url);
    }
}
