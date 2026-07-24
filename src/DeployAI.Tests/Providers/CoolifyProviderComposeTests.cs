using System.Net;
using System.Text.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Providers.Coolify;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

public class CoolifyProviderComposeTests
{
    private const string InstanceUrl = "https://coolify.example.com";
    private static readonly ProviderCredentials Credentials =
        new(CoolifyCredentialStorage.Serialize(InstanceUrl, "coolify-token"));

    // Coolify has no /applications/dockercompose route — a live v4.1.2 instance 404s it while
    // answering /applications/public. A compose app is created through the same git-backed
    // endpoint as any other; build_pack is what makes it compose.
    [Fact]
    public async Task CreateProjectAsync_CreatesComposeAppThroughThePublicEndpoint()
    {
        var handler = new MockHttpMessageHandler();
        StubInfrastructure(handler);
        var createBody = CaptureJson(
            handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/public"),
            HttpStatusCode.Created,
            """{ "uuid": "app-compose" }""");
        StubApplication(handler, "app-compose");

        var provider = new CoolifyProvider(handler.ToHttpClient());
        await provider.CreateProjectAsync(Credentials, ComposeRequest(), CancellationToken.None);

        // Not the default docker-compose.yml — that is usually the repo's local dev stack.
        // Rooted at the repo: Coolify rejects a bare relative path.
        Assert.Equal("/docker-compose.coolify.yml", createBody.Value.GetProperty("docker_compose_location").GetString());
        Assert.Equal("dockercompose", createBody.Value.GetProperty("build_pack").GetString());
    }

    [Fact]
    public async Task CreateProjectAsync_ForCompose_OmitsExposedPortAndSingleAppBuildFields()
    {
        var handler = new MockHttpMessageHandler();
        StubInfrastructure(handler);
        var createBody = CaptureJson(
            handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/public"),
            HttpStatusCode.Created,
            """{ "uuid": "app-compose" }""");
        StubApplication(handler, "app-compose");

        var provider = new CoolifyProvider(handler.ToHttpClient());
        await provider.CreateProjectAsync(
            Credentials,
            ComposeRequest() with
            {
                OutputDirectory = "dist/app/browser",
                BuildCommand = "npm run build",
                DockerfilePath = "client/Dockerfile"
            },
            CancellationToken.None);

        var body = createBody.Value;
        // The compose file declares its own ports; a single number would be meaningless.
        Assert.False(body.TryGetProperty("ports_exposes", out _));
        // Compose builds its own images, so these describe a build Coolify is not running.
        Assert.False(body.TryGetProperty("publish_directory", out _));
        Assert.False(body.TryGetProperty("is_static", out _));
        Assert.False(body.TryGetProperty("build_command", out _));
        Assert.False(body.TryGetProperty("dockerfile_location", out _));
    }

    // A compose domain is attached post-deploy, not at create: Coolify won't accept
    // docker_compose_domains until the first deploy has parsed the compose file.
    [Fact]
    public async Task AssignComposeDomainAsync_AttachesDomainToTheNamedService()
    {
        var handler = new MockHttpMessageHandler();
        var patchBody = CaptureJson(
            handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-compose"),
            HttpStatusCode.OK,
            """{ "uuid": "app-compose" }""");

        var provider = new CoolifyProvider(handler.ToHttpClient());
        await provider.AssignComposeDomainAsync(
            Credentials, "app-compose", "breeze.example.com", "web", CancellationToken.None);

        var web = patchBody.Value.GetProperty("docker_compose_domains").GetProperty("web");
        Assert.Equal("https://breeze.example.com", web.GetProperty("domain").GetString());
        // Coolify rejects the entry without the service named inside it as well as keyed by it.
        Assert.Equal("web", web.GetProperty("name").GetString());
        // A compose app must not carry a top-level domains/fqdn field.
        Assert.False(patchBody.Value.TryGetProperty("domains", out _));
        Assert.False(patchBody.Value.TryGetProperty("fqdn", out _));
    }

    [Fact]
    public async Task AssignComposeDomainAsync_DefaultsToTheWebService()
    {
        var handler = new MockHttpMessageHandler();
        var patchBody = CaptureJson(
            handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-compose"),
            HttpStatusCode.OK,
            """{ "uuid": "app-compose" }""");

        var provider = new CoolifyProvider(handler.ToHttpClient());
        await provider.AssignComposeDomainAsync(
            Credentials, "app-compose", "breeze.example.com", serviceName: null, CancellationToken.None);

        Assert.True(patchBody.Value.GetProperty("docker_compose_domains").TryGetProperty("web", out _));
    }

    // The compose domain is deferred until after the first deploy, so create must not PATCH it.
    [Fact]
    public async Task CreateProjectAsync_DoesNotAttachTheComposeDomain()
    {
        var handler = new MockHttpMessageHandler();
        StubInfrastructure(handler);
        handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/public")
            .Respond(HttpStatusCode.Created, "application/json", """{ "uuid": "app-compose" }""");
        var patched = false;
        handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-compose")
            .Respond(_ => { patched = true; return new HttpResponseMessage(HttpStatusCode.OK); });
        StubApplication(handler, "app-compose");

        var provider = new CoolifyProvider(handler.ToHttpClient());
        await provider.CreateProjectAsync(
            Credentials,
            ComposeRequest() with { CustomDomain = "breeze.example.com" },
            CancellationToken.None);

        Assert.False(patched);
    }

    [Fact]
    public async Task CreateProjectAsync_DefersFirstDeploy_SoDomainAndEnvLandFirst()
    {
        var handler = new MockHttpMessageHandler();
        StubInfrastructure(handler);
        var createBody = CaptureJson(
            handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/public"),
            HttpStatusCode.Created,
            """{ "uuid": "app-compose" }""");
        StubApplication(handler, "app-compose");

        var provider = new CoolifyProvider(handler.ToHttpClient());
        await provider.CreateProjectAsync(Credentials, ComposeRequest(), CancellationToken.None);

        Assert.False(createBody.Value.GetProperty("instant_deploy").GetBoolean());
        Assert.True(createBody.Value.GetProperty("is_auto_deploy_enabled").GetBoolean());
    }

    [Fact]
    public async Task AssignComposeDomainAsync_FailsLoudly_WhenRejected()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Patch, $"{InstanceUrl}/api/v1/applications/app-compose")
            .Respond(HttpStatusCode.UnprocessableEntity, "application/json", """{ "message": "Domain already in use." }""");

        var provider = new CoolifyProvider(handler.ToHttpClient());
        var error = await Assert.ThrowsAsync<DeployAIException>(() => provider.AssignComposeDomainAsync(
            Credentials, "app-compose", "breeze.example.com", "web", CancellationToken.None));

        Assert.Equal("coolify_domain_failed", error.ErrorCode);
        Assert.Contains("Domain already in use.", error.Message, StringComparison.Ordinal);
    }

    // Deploying into whichever project Coolify listed first is a silent way to put an app
    // somewhere the user never chose.
    [Fact]
    public async Task CreateProjectAsync_RefusesToGuess_WhenSeveralProjectsExist()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", """
            [{ "uuid": "proj-1", "name": "Staging" }, { "uuid": "proj-2", "name": "Production" }]
            """);

        var provider = new CoolifyProvider(handler.ToHttpClient());
        var error = await Assert.ThrowsAsync<DeployAIException>(() => provider.CreateProjectAsync(
            Credentials, ComposeRequest(), CancellationToken.None));

        Assert.Equal("coolify_project_ambiguous", error.ErrorCode);
        Assert.Contains("Staging", error.Message, StringComparison.Ordinal);
        Assert.Contains("Production", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateProjectAsync_RefusesToGuess_WhenSeveralServersExist()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "proj-1", "name": "Main" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
            .Respond(HttpStatusCode.OK, "application/json", """
            [{ "uuid": "srv-1", "name": "hetzner-1" }, { "uuid": "srv-2", "name": "hetzner-2" }]
            """);

        var provider = new CoolifyProvider(handler.ToHttpClient());
        var error = await Assert.ThrowsAsync<DeployAIException>(() => provider.CreateProjectAsync(
            Credentials, ComposeRequest(), CancellationToken.None));

        Assert.Equal("coolify_server_ambiguous", error.ErrorCode);
        Assert.Contains("hetzner-2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateProjectAsync_UsesPrivateGithubAppEndpoint_ForPrivateComposeRepos()
    {
        var handler = new MockHttpMessageHandler();
        StubInfrastructure(handler);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/github-apps")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "gh-1", "name": "Deploy" }]""");
        var createBody = CaptureJson(
            handler.When(HttpMethod.Post, $"{InstanceUrl}/api/v1/applications/private-github-app"),
            HttpStatusCode.Created,
            """{ "uuid": "app-compose" }""");
        StubApplication(handler, "app-compose");

        var provider = new CoolifyProvider(handler.ToHttpClient());
        var project = await provider.CreateProjectAsync(
            Credentials,
            ComposeRequest() with { IsPrivateRepository = true },
            CancellationToken.None);

        Assert.Equal("app-compose", project.Id);
        Assert.Equal("dockercompose", createBody.Value.GetProperty("build_pack").GetString());
    }

    private static CreateProviderProjectRequest ComposeRequest() =>
        new(
            "yemeni-breeze",
            "acme/yemeni-breeze",
            "angular",
            GitBranch: "main",
            ComposeFileLocation: "docker-compose.coolify.yml");

    private static void StubInfrastructure(MockHttpMessageHandler handler)
    {
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "proj-1", "name": "Main" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "srv-1", "name": "hetzner-1" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects/proj-1/environments")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "env-1", "name": "production" }]""");
    }

    private static void StubApplication(MockHttpMessageHandler handler, string uuid)
    {
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/{uuid}")
            .Respond(HttpStatusCode.OK, "application/json", $$"""
            { "uuid": "{{uuid}}", "name": "yemeni-breeze", "fqdn": "https://breeze.example.com", "git_branch": "main" }
            """);
    }

    private sealed class JsonCapture
    {
        public JsonElement Value { get; set; }
    }

    private static JsonCapture CaptureJson(
        MockedRequest request,
        HttpStatusCode status,
        string responseJson)
    {
        var capture = new JsonCapture();
        request.Respond(async httpRequest =>
        {
            var body = await httpRequest.Content!.ReadAsStringAsync();
            capture.Value = JsonDocument.Parse(body).RootElement.Clone();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };
        });
        return capture;
    }
}
