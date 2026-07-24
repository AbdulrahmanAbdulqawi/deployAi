using System.Net;
using System.Net.Http.Json;
using DeployAI.Core.Providers;
using DeployAI.Providers.Coolify;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

public class CoolifyProviderContractTests
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
    public async Task ValidateCredentialsAsync_ReturnsTrue_WhenHealthcheckAndApplicationsSucceed()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/health")
            .Respond(HttpStatusCode.OK, "text/html", "OK");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications")
            .Respond(HttpStatusCode.OK, "application/json", "[]");

        var provider = CreateProvider(handler);
        var result = await provider.ValidateCredentialsAsync(Credentials, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ReturnsFalse_WhenApplicationsFail()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/health")
            .Respond(HttpStatusCode.OK, "text/html", "OK");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications")
            .Respond(HttpStatusCode.Forbidden);

        var provider = CreateProvider(handler);
        var result = await provider.ValidateCredentialsAsync(Credentials, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ReturnsFalse_WhenHealthcheckFails()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/health")
            .Respond(HttpStatusCode.Unauthorized);

        var provider = CreateProvider(handler);
        var result = await provider.ValidateCredentialsAsync(Credentials, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task ListProjectsAsync_MapsApplications()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications")
            .Respond(HttpStatusCode.OK, "application/json", """
            [
              { "uuid": "app-uuid-1", "name": "My App", "fqdn": "https://app.example.com", "git_branch": "main" }
            ]
            """);

        var provider = CreateProvider(handler);
        var projects = await provider.ListProjectsAsync(Credentials, CancellationToken.None);

        Assert.Single(projects);
        Assert.Equal("app-uuid-1", projects[0].Id);
        Assert.Equal("My App", projects[0].Name);
        Assert.Equal("https://app.example.com", projects[0].Url);
        Assert.Equal("main", projects[0].GitBranch);
    }

    [Fact]
    public async Task TriggerDeploymentAsync_ReturnsDeploymentId()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/deploy")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "deployments": [
                {
                  "deployment_uuid": "dep-123",
                  "message": "Deployment request queued."
                }
              ]
            }
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-uuid-1")
            .Respond(HttpStatusCode.OK, "application/json", """
            { "uuid": "app-uuid-1", "fqdn": "https://app.example.com" }
            """);

        var provider = CreateProvider(handler);
        var response = await provider.TriggerDeploymentAsync(
            Credentials,
            "app-uuid-1",
            "main",
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.Equal("dep-123", response.DeploymentId);
        Assert.Equal("https://app.example.com", response.DeployUrl);
    }

    [Fact]
    public async Task GetStatusAsync_MapsFinishedToSuccess()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/deployments/dep-123")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "deployment_uuid": "dep-123",
              "status": "finished",
              "deployment_url": "project/abc/environment/def/application/ghi/deployment/dep-123"
            }
            """);

        var provider = CreateProvider(handler);
        var status = await provider.GetStatusAsync(Credentials, "dep-123", CancellationToken.None);

        Assert.Equal(DeploymentStatusKind.Success, status.Status);
        // deployment_url is a Coolify page path, not the app's address. Normalizing it used
        // to fabricate "https://project/..." — status must not carry a URL at all.
        Assert.Null(status.DeployUrl);
    }

    [Fact]
    public async Task GetStatusAsync_FailedFlagWithCompletedRollout_MapsToSuccess()
    {
        // Verbatim tail of live deployment nmt57rvx35n4dla0n4t9de23 (2026-07-24): the API
        // reported status "failed" while this log shows the rollout completed — and the
        // Coolify UI later showed the same deployment as "Finished". The log wins.
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/deployments/dep-123")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "deployment_uuid": "dep-123",
              "status": "failed",
              "logs": "[{\"output\":\"Removing old containers.\"},{\"output\":\"Starting new application.\"},{\"output\":\"Container api-poffi-134628548659  Started\"},{\"output\":\"Container web-poffi-134628563306  Started\"},{\"output\":\"New container started.\"},{\"output\":\"Gracefully shutting down build container: nmt57rvx35n4dla0n4t9de23\"}]"
            }
            """);

        var provider = CreateProvider(handler);
        var status = await provider.GetStatusAsync(Credentials, "dep-123", CancellationToken.None);

        Assert.Equal(DeploymentStatusKind.Success, status.Status);
    }

    [Fact]
    public async Task GetStatusAsync_FailedFlagWithoutRollout_StaysFailed()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/deployments/dep-123")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "deployment_uuid": "dep-123",
              "status": "failed",
              "logs": "[{\"output\":\"Building docker image.\"},{\"output\":\"esbuild: error - build failed\"}]"
            }
            """);

        var provider = CreateProvider(handler);
        var status = await provider.GetStatusAsync(Credentials, "dep-123", CancellationToken.None);

        Assert.Equal(DeploymentStatusKind.Failed, status.Status);
        Assert.Equal("esbuild: error - build failed", status.ErrorMessage);
    }

    [Fact]
    public async Task StreamLogsAsync_YieldsNewLogLines()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/deployments/dep-123")
            .Respond(req =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        deployment_uuid = "dep-123",
                        status = "in_progress",
                        logs = "Building image\nDeploying container\n"
                    })
                };
                return response;
            });

        var provider = CreateProvider(handler);
        var lines = new List<string>();
        await foreach (var line in provider.StreamLogsAsync(Credentials, "dep-123", CancellationToken.None))
        {
            lines.Add(line);
            if (lines.Count >= 2)
            {
                break;
            }
        }

        Assert.Contains("Building image", lines);
        Assert.Contains("Deploying container", lines);
    }
}
