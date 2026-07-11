using System.Net;
using System.Net.Http.Json;
using DeployAI.Core.Providers;
using DeployAI.Providers.Coolify;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

public class CoolifyProviderServiceOperationsTests
{
    private const string InstanceUrl = "https://coolify.example.com";
    private const string ApiToken = "coolify-token";
    private static readonly ProviderCredentials Credentials =
        new(CoolifyCredentialStorage.Serialize(InstanceUrl, ApiToken));

    private static CoolifyProvider CreateProvider(MockHttpMessageHandler handler)
    {
        var client = handler.ToHttpClient();
        return new CoolifyProvider(client);
    }

    [Fact]
    public async Task GetServiceStatusAsync_ReturnsRunning_WhenApplicationHasFqdn()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-1")
            .Respond(HttpStatusCode.OK, "application/json", """
            { "uuid": "app-1", "name": "web", "fqdn": "https://web.example.com", "status": "running" }
            """);

        var provider = CreateProvider(handler);
        var status = await provider.GetServiceStatusAsync(Credentials, "app-1", CancellationToken.None);

        Assert.Equal("running", status.Status);
        Assert.Equal("https://web.example.com", status.DeployUrl);
    }

    [Fact]
    public async Task RedeployServiceAsync_PostsDeployRequest()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/deploy")
            .Respond(HttpStatusCode.OK, "application/json", """
            { "deployments": [{ "deployment_uuid": "dep-1" }] }
            """);

        var provider = CreateProvider(handler);
        await provider.RedeployServiceAsync(Credentials, "app-1", CancellationToken.None);

        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task DeleteServiceAsync_DeletesApplication()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Delete, $"{InstanceUrl}/api/v1/applications/app-1")
            .Respond(HttpStatusCode.NoContent);

        var provider = CreateProvider(handler);
        await provider.DeleteServiceAsync(Credentials, "app-1", CancellationToken.None);

        handler.VerifyNoOutstandingExpectation();
    }
}
