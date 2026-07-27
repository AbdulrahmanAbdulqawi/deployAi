using System.Net;
using System.Net.Http.Json;
using DeployAI.Core.Exceptions;
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
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
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
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
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
        handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-1/envs/bulk")
            .Respond(HttpStatusCode.Created, "application/json", """
            [{ "uuid": "env-1", "key": "API_URL", "value": "https://api.example.com" }]
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

    [Fact]
    public async Task UpsertEnvVarAsync_GoesThroughBulkEndpointWithoutReadingFirst()
    {
        // Regression: the previous implementation listed the app's env vars and then chose POST
        // (create) or PATCH (update) from what it saw. Two syncs could both read "absent" and
        // both create, and Coolify's create endpoint does not dedupe by key -- one application
        // ended up with 32 records for 16 keys, including two DATABASE_URLs pointing at
        // different Postgres instances.
        //
        // The list call is what makes the write non-atomic, so assert it does not happen at all:
        // the whole upsert must be the single server-side /envs/bulk request. MockHttp throws on
        // any request without a matching expectation, so a reintroduced GET fails here.
        var handler = new MockHttpMessageHandler();
        var bulk = handler.Expect(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-1/envs/bulk")
            .WithPartialContent("\"key\":\"DATABASE_URL\"")
            .WithPartialContent("postgres://user:pw@db:5432/app")
            .Respond(HttpStatusCode.Created, "application/json", """
            [{ "uuid": "env-9", "key": "DATABASE_URL", "value": "postgres://user:pw@db:5432/app" }]
            """);

        var provider = CreateProvider(handler);
        var envVar = await provider.UpsertEnvVarAsync(
            Credentials,
            "app-1",
            new UpsertProviderEnvVarRequest(
                "DATABASE_URL", "postgres://user:pw@db:5432/app", "plain", []),
            CancellationToken.None);

        Assert.Equal("env-9", envVar.Id);
        Assert.Equal(1, handler.GetMatchCount(bulk));
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task UpsertEnvVarAsync_SurfacesCoolifyErrorMessage()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-1/envs/bulk")
            .Respond(HttpStatusCode.NotFound, "application/json", """
            { "message": "Application not found." }
            """);

        var provider = CreateProvider(handler);
        var ex = await Assert.ThrowsAsync<DeployAIException>(() => provider.UpsertEnvVarAsync(
            Credentials,
            "app-1",
            new UpsertProviderEnvVarRequest("API_URL", "https://api.example.com", "plain", []),
            CancellationToken.None));

        Assert.Contains("Application not found.", ex.Message);
    }

    [Fact]
    public async Task UpsertEnvVarAsync_TreatsUnexpectedResponseShapeAsApplied()
    {
        // The write has already succeeded by the time the body is parsed, so an unfamiliar shape
        // must not fail the call -- it only costs us the uuid.
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-1/envs/bulk")
            .Respond(HttpStatusCode.Created, "application/json", """
            { "message": "Environment variables updated." }
            """);

        var provider = CreateProvider(handler);
        var envVar = await provider.UpsertEnvVarAsync(
            Credentials,
            "app-1",
            new UpsertProviderEnvVarRequest("API_URL", "https://api.example.com", "plain", []),
            CancellationToken.None);

        Assert.Equal("API_URL", envVar.Key);
        Assert.Equal("API_URL", envVar.Id);
    }

    [Fact]
    public async Task ListInfrastructureAsync_ReturnsProjectsServersAndGithubApps()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "proj-1", "name": "Main" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
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
    public async Task UpdateApplicationConfigAsync_PatchesBuildPackPortAndClearsCustomLabels()
    {
        var handler = new MockHttpMessageHandler();
        string? capturedBody = null;
        handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-1")
            .Respond(request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "uuid": "app-1" }""", System.Text.Encoding.UTF8, "application/json")
                };
            });

        var provider = CreateProvider(handler);
        await provider.UpdateApplicationConfigAsync(
            Credentials,
            "app-1",
            new UpdateProviderApplicationConfigRequest(
                Framework: "angular",
                RootDirectory: "client",
                OutputDirectory: "dist/app/browser",
                BuildCommand: "npm run build"),
            CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var document = System.Text.Json.JsonDocument.Parse(capturedBody!);
        var root = document.RootElement;
        Assert.Equal("nixpacks", root.GetProperty("build_pack").GetString());
        Assert.Equal("3000", root.GetProperty("ports_exposes").GetString());
        Assert.Equal("W10=", root.GetProperty("custom_labels").GetString());
        Assert.Equal("/client", root.GetProperty("base_directory").GetString());
        Assert.Equal("/dist/app/browser", root.GetProperty("publish_directory").GetString());
        Assert.Equal("npm run build", root.GetProperty("build_command").GetString());
    }

    [Fact]
    public async Task UpdateApplicationConfigAsync_ResolvesDockerfileBuildPackAndPort()
    {
        var handler = new MockHttpMessageHandler();
        string? capturedBody = null;
        handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-2")
            .Respond(request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "uuid": "app-2" }""", System.Text.Encoding.UTF8, "application/json")
                };
            });

        var provider = CreateProvider(handler);
        await provider.UpdateApplicationConfigAsync(
            Credentials,
            "app-2",
            new UpdateProviderApplicationConfigRequest(
                Framework: "dotnet",
                DockerfilePath: "src/Api/Dockerfile"),
            CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var document = System.Text.Json.JsonDocument.Parse(capturedBody!);
        var root = document.RootElement;
        Assert.Equal("dockerfile", root.GetProperty("build_pack").GetString());
        Assert.Equal("8080", root.GetProperty("ports_exposes").GetString());
        Assert.Equal("W10=", root.GetProperty("custom_labels").GetString());
        Assert.Equal("src/Api/Dockerfile", root.GetProperty("dockerfile_location").GetString());
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
