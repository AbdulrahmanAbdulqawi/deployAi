using System.Net;
using System.Net.Http.Json;
using DeployAI.Core.Providers;
using DeployAI.Providers.Vercel;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

public class VercelProviderContractTests
{
    private static VercelProvider CreateProvider(MockHttpMessageHandler handler)
    {
        var client = handler.ToHttpClient();
        client.BaseAddress = new Uri("https://api.vercel.com/");
        return new VercelProvider(client);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ReturnsTrue_WhenUserEndpointSucceeds()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v2/user")
            .Respond(HttpStatusCode.OK, "application/json", "{}");

        var provider = CreateProvider(handler);
        var result = await provider.ValidateCredentialsAsync(new ProviderCredentials("token"), CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ReturnsFalse_WhenUserEndpointFails()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v2/user")
            .Respond(HttpStatusCode.Unauthorized);

        var provider = CreateProvider(handler);
        var result = await provider.ValidateCredentialsAsync(new ProviderCredentials("bad"), CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task ListProjectsAsync_MapsProjects()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects*")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "projects": [
                { "id": "prj_1", "name": "My App" }
              ]
            }
            """);

        var provider = CreateProvider(handler);
        var projects = await provider.ListProjectsAsync(new ProviderCredentials("token"), CancellationToken.None);
        Assert.Single(projects);
        Assert.Equal("prj_1", projects[0].Id);
        Assert.Equal("My App", projects[0].Name);
    }

    [Fact]
    public async Task TriggerDeploymentAsync_ReturnsDeploymentId()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/my-app")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "id": "prj_1",
              "name": "my-app",
              "link": { "type": "github", "repo": "owner/my-app", "repoId": 123456789 }
            }
            """);
        handler.When(HttpMethod.Patch, "https://api.vercel.com/v9/projects/prj_1")
            .Respond(HttpStatusCode.OK, "application/json", "{}");
        handler.When(HttpMethod.Post, "https://api.vercel.com/v13/deployments*")
            .Respond(HttpStatusCode.OK, "application/json", """
            { "id": "dpl_123", "url": "my-app.vercel.app" }
            """);

        var provider = CreateProvider(handler);
        var response = await provider.TriggerDeploymentAsync(
            new ProviderCredentials("token"),
            "my-app",
            "main",
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.Equal("dpl_123", response.DeploymentId);
        Assert.Equal("https://my-app.vercel.app", response.DeployUrl);
    }

    [Fact]
    public async Task GetStatusAsync_MapsReadyStateToSuccess()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v13/deployments/dpl_123")
            .Respond(HttpStatusCode.OK, "application/json", """
            { "id": "dpl_123", "readyState": "READY", "url": "my-app.vercel.app" }
            """);

        var provider = CreateProvider(handler);
        var status = await provider.GetStatusAsync(new ProviderCredentials("token"), "dpl_123", CancellationToken.None);
        Assert.Equal(DeploymentStatusKind.Success, status.Status);
        Assert.Equal("https://my-app.vercel.app", status.DeployUrl);
    }

    [Fact]
    public async Task CreateProjectAsync_MapsCreatedProject()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://api.vercel.com/v11/projects")
            .Respond(HttpStatusCode.OK, "application/json", """
            { "id": "prj_new", "name": "my-app" }
            """);

        var provider = CreateProvider(handler);
        var project = await provider.CreateProjectAsync(
            new ProviderCredentials("token"),
            new CreateProviderProjectRequest("My-App", "owner/my-app", null),
            CancellationToken.None);

        Assert.Equal("prj_new", project.Id);
        Assert.Equal("my-app", project.Name);
    }

    [Fact]
    public async Task CreateProjectAsync_SetsRootDirectory_WhenProvided()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://api.vercel.com/v11/projects")
            .Respond(HttpStatusCode.OK, "application/json", """
            { "id": "prj_new", "name": "my-app" }
            """);
        handler.When(HttpMethod.Patch, "https://api.vercel.com/v9/projects/prj_new")
            .With(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return body.Contains("\"rootDirectory\":\"client\"", StringComparison.Ordinal) &&
                       body.Contains("\"outputDirectory\":\"dist/client/browser\"", StringComparison.Ordinal);
            })
            .Respond(HttpStatusCode.OK, "application/json", "{}");

        var provider = CreateProvider(handler);
        await provider.CreateProjectAsync(
            new ProviderCredentials("token"),
            new CreateProviderProjectRequest("My-App", "owner/my-app", null, "client"),
            CancellationToken.None);

        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task TriggerDeploymentAsync_IncludesRootDirectoryInProjectSettings()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v9/projects/my-app")
            .Respond(HttpStatusCode.OK, "application/json", """
            {
              "id": "prj_1",
              "name": "my-app",
              "link": { "type": "github", "repo": "owner/my-app", "repoId": 123456789 }
            }
            """);
        handler.When(HttpMethod.Patch, "https://api.vercel.com/v9/projects/prj_1")
            .With(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return body.Contains("\"rootDirectory\":\"client\"", StringComparison.Ordinal) &&
                       body.Contains("\"outputDirectory\":\"dist/client/browser\"", StringComparison.Ordinal);
            })
            .Respond(HttpStatusCode.OK, "application/json", "{}");
        handler.When(HttpMethod.Post, "https://api.vercel.com/v13/deployments*")
            .With(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return body.Contains("\"rootDirectory\":\"client\"", StringComparison.Ordinal) &&
                       body.Contains("\"outputDirectory\":\"dist/client/browser\"", StringComparison.Ordinal);
            })
            .Respond(HttpStatusCode.OK, "application/json", """
            { "id": "dpl_123", "url": "my-app.vercel.app" }
            """);

        var provider = CreateProvider(handler);
        await provider.TriggerDeploymentAsync(
            new ProviderCredentials("token"),
            "my-app",
            "main",
            new Dictionary<string, string> { ["rootDirectory"] = "client" },
            CancellationToken.None);
    }

    [Fact]
    public async Task CreateProjectAsync_ThrowsFriendlyError_WhenGitHubIntegrationMissing()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://api.vercel.com/v11/projects")
            .Respond(HttpStatusCode.BadRequest, "application/json", """
            {
              "error": {
                "code": "bad_request",
                "message": "To link a GitHub repository, you need to install the GitHub integration first."
              }
            }
            """);

        var provider = CreateProvider(handler);
        var ex = await Assert.ThrowsAsync<DeployAI.Core.Exceptions.DeployAIException>(() =>
            provider.CreateProjectAsync(
                new ProviderCredentials("token"),
                new CreateProviderProjectRequest("my-app", "owner/my-app", null),
                CancellationToken.None));

        Assert.Equal("vercel_api_error", ex.ErrorCode);
        Assert.Contains("github.com/apps/vercel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListEnvVarsAsync_MasksSecretValues()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, "https://api.vercel.com/v10/projects/prj_1/env")
            .Respond(HttpStatusCode.OK, "application/json", """
            [
              { "id": "env_1", "key": "PUBLIC_URL", "value": "https://example.com", "type": "plain", "target": ["production"] },
              { "id": "env_2", "key": "API_KEY", "value": "secret-value", "type": "secret", "target": ["production"] }
            ]
            """);

        var provider = CreateProvider(handler);
        var envVars = await provider.ListEnvVarsAsync(new ProviderCredentials("token"), "prj_1", CancellationToken.None);

        Assert.Equal(2, envVars.Count);
        Assert.Equal("https://example.com", envVars[0].Value);
        Assert.False(envVars[0].ValueHidden);
        Assert.Null(envVars[1].Value);
        Assert.True(envVars[1].ValueHidden);
    }

    [Fact]
    public async Task UpsertEnvVarAsync_ReturnsCreatedEnvVar()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://api.vercel.com/v10/projects/prj_1/env*")
            .Respond(HttpStatusCode.OK, "application/json", """
            [{ "id": "env_new", "key": "API_URL", "value": "https://api.example.com", "type": "encrypted", "target": ["production"] }]
            """);

        var provider = CreateProvider(handler);
        var envVar = await provider.UpsertEnvVarAsync(
            new ProviderCredentials("token"),
            "prj_1",
            new UpsertProviderEnvVarRequest("API_URL", "https://api.example.com", "encrypted", ["production"]),
            CancellationToken.None);

        Assert.Equal("env_new", envVar.Id);
        Assert.Equal("API_URL", envVar.Key);
    }

    [Fact]
    public async Task DeleteEnvVarAsync_Succeeds()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Delete, "https://api.vercel.com/v10/projects/prj_1/env/env_1")
            .Respond(HttpStatusCode.OK);

        var provider = CreateProvider(handler);
        await provider.DeleteEnvVarAsync(new ProviderCredentials("token"), "prj_1", "env_1", CancellationToken.None);
    }
}
