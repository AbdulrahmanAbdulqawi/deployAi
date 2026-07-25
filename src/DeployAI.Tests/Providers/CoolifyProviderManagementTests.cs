using System.Net;
using System.Net.Http.Json;
using DeployAI.Core.Providers;
using DeployAI.Providers.Coolify;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

public class CoolifyProviderManagementTests
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
    public async Task CreateProjectAsync_CreatesPublicApplication()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", """
            [{ "uuid": "proj-1", "name": "Main" }]
            """);
        // No pre-existing application: creating is not idempotent on Coolify, so the provider
        // checks for one with this name and repository before adding another.
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications")
            .Respond(HttpStatusCode.OK, "application/json", "[]");        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
            .Respond(HttpStatusCode.OK, "application/json", """
            [{ "uuid": "server-1", "name": "localhost" }]
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects/proj-1/environments")
            .Respond(HttpStatusCode.OK, "application/json", """
            [{ "uuid": "env-1", "name": "production" }]
            """);
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/public")
            .Respond(HttpStatusCode.Created, "application/json", """
            { "uuid": "app-new" }
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-new")
            .Respond(HttpStatusCode.OK, "application/json", """
            { "uuid": "app-new", "name": "my-app", "fqdn": "https://my-app.example.com", "git_branch": "main" }
            """);

        var provider = CreateProvider(handler);
        var project = await provider.CreateProjectAsync(
            Credentials,
            new CreateProviderProjectRequest(
                "my-app",
                "acme/widget",
                null,
                GitBranch: "main"),
            CancellationToken.None);

        Assert.Equal("app-new", project.Id);
        Assert.Equal("my-app", project.Name);
        Assert.Equal("https://my-app.example.com", project.Url);
        Assert.Equal("main", project.GitBranch);
    }

    [Fact]
    public async Task CreateProjectAsync_UsesPrivateGithubAppEndpoint()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.Created, "application/json", """{ "uuid": "proj-new" }""");
        // No pre-existing application: creating is not idempotent on Coolify, so the provider
        // checks for one with this name and repository before adding another.
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications")
            .Respond(HttpStatusCode.OK, "application/json", "[]");        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "server-1", "name": "localhost" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects/proj-new/environments")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "env-1", "name": "production" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/github-apps")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "gh-app-1", "name": "Deploy" }]""");
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/private-github-app")
            .Respond(HttpStatusCode.Created, "application/json", """{ "uuid": "app-private" }""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-private")
            .Respond(HttpStatusCode.OK, "application/json", """{ "uuid": "app-private", "name": "private-app" }""");

        var provider = CreateProvider(handler);
        var project = await provider.CreateProjectAsync(
            Credentials,
            new CreateProviderProjectRequest(
                "private-app",
                "acme/private-repo",
                null,
                GitBranch: "develop",
                IsPrivateRepository: true),
            CancellationToken.None);

        Assert.Equal("app-private", project.Id);
        Assert.Equal("private-app", project.Name);
    }

    [Fact]
    public async Task UpsertEnvVarAsync_CreatesWhenMissing()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-1/envs")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/app-1/envs")
            .Respond(HttpStatusCode.Created, "application/json", """
            { "uuid": "env-1", "key": "API_URL", "value": "https://api.example.com" }
            """);

        var provider = CreateProvider(handler);
        var envVar = await provider.UpsertEnvVarAsync(
            Credentials,
            "app-1",
            new UpsertProviderEnvVarRequest("API_URL", "https://api.example.com", "plain", []),
            CancellationToken.None);

        Assert.Equal("API_URL", envVar.Key);
        Assert.Equal("env-1", envVar.Id);
    }

    // Type and Targets used to be accepted and silently dropped, so a secret landed in Coolify
    // as a readable plain value and a build-time variable was never available to the build.
    [Fact]
    public async Task UpsertEnvVarAsync_SendsSecretAndPreviewFlags()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-1/envs")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        var sent = CaptureEnvVarPost(handler, "env-1", "JWT_KEY");

        var provider = CreateProvider(handler);
        await provider.UpsertEnvVarAsync(
            Credentials,
            "app-1",
            new UpsertProviderEnvVarRequest("JWT_KEY", "s3cret-value", ProviderEnvVarTypes.Secret, ["preview"]),
            CancellationToken.None);

        var body = System.Text.Json.JsonDocument.Parse(sent.Body!).RootElement;
        Assert.True(body.GetProperty("is_shown_once").GetBoolean());
        Assert.True(body.GetProperty("is_preview").GetBoolean());
        // Coolify interpolates unescaped values; literal keeps $ and {} intact in generated keys.
        Assert.True(body.GetProperty("is_literal").GetBoolean());
        Assert.False(body.GetProperty("is_multiline").GetBoolean());
    }

    [Fact]
    public async Task UpsertEnvVarAsync_MarksBuildTimeVariables()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/app-1/envs")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        var sent = CaptureEnvVarPost(handler, "env-2", "API_BASE_URL");

        var provider = CreateProvider(handler);
        await provider.UpsertEnvVarAsync(
            Credentials,
            "app-1",
            new UpsertProviderEnvVarRequest(
                "API_BASE_URL", "https://api.example.com", ProviderEnvVarTypes.BuildTime, []),
            CancellationToken.None);

        var body = System.Text.Json.JsonDocument.Parse(sent.Body!).RootElement;
        Assert.True(body.GetProperty("is_build_time").GetBoolean());
        Assert.False(body.TryGetProperty("is_shown_once", out _));
    }

    private sealed class SentBody
    {
        public string? Body { get; set; }
    }

    private static SentBody CaptureEnvVarPost(MockHttpMessageHandler handler, string uuid, string key)
    {
        var sent = new SentBody();
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/app-1/envs")
            .Respond(async request =>
            {
                sent.Body = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        $$"""{ "uuid": "{{uuid}}", "key": "{{key}}" }""",
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            });
        return sent;
    }

    [Fact]
    public async Task ListInfrastructureAsync_ReturnsProjectsServersAndGithubApps()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "proj-1", "name": "Main" }]""");
        // No pre-existing application: creating is not idempotent on Coolify, so the provider
        // checks for one with this name and repository before adding another.
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications")
            .Respond(HttpStatusCode.OK, "application/json", "[]");        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "server-1", "name": "localhost" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/github-apps")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "gh-1", "name": "GitHub" }]""");

        var provider = CreateProvider(handler);
        var infrastructure = await provider.ListInfrastructureAsync(Credentials, CancellationToken.None);

        Assert.Single(infrastructure.Projects);
        Assert.Equal("proj-1", infrastructure.Projects[0].Id);
        Assert.Single(infrastructure.Servers);
        Assert.Single(infrastructure.GithubApps);
    }

    [Fact]
    public async Task ListProjectEnvironmentsAsync_ReturnsEnvironments()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects/proj-1/environments")
            .Respond(HttpStatusCode.OK, "application/json", """
            [
              { "uuid": "env-1", "name": "production" },
              { "uuid": "env-2", "name": "staging" }
            ]
            """);

        var provider = CreateProvider(handler);
        var environments = await provider.ListProjectEnvironmentsAsync(
            Credentials,
            "proj-1",
            CancellationToken.None);

        Assert.Equal(2, environments.Count);
        Assert.Equal("production", environments[0].Name);
        Assert.Equal("staging", environments[1].Name);
    }
}
