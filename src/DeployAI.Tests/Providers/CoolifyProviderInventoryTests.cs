using System.Net;
using DeployAI.Core.Providers;
using DeployAI.Providers.Coolify;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

/// <summary>
/// The inventory joins four listings Coolify exposes separately — projects, each project's
/// environments, all applications, all databases — on the numeric environment id that is the
/// only link Coolify returns. Payloads here are shaped like a real instance's (2026-09-21):
/// a compose app routed through <c>docker_compose_domains</c>, a Dockerfile app with two fqdns,
/// and a crash-looped one.
/// </summary>
public class CoolifyProviderInventoryTests
{
    private const string InstanceUrl = "https://coolify.example.com";
    private static readonly ProviderCredentials Credentials =
        new(CoolifyCredentialStorage.Serialize(InstanceUrl, "coolify-token"));

    private static CoolifyProvider CreateProvider(MockHttpMessageHandler handler) =>
        new(handler.ToHttpClient());

    private static MockHttpMessageHandler InstanceWithTwoProjects()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", """
            [{ "uuid": "proj-nabdah", "name": "nabdah" }, { "uuid": "proj-portfolio", "name": "portfolio" }]
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects/proj-nabdah/environments")
            .Respond(HttpStatusCode.OK, "application/json", """
            [{ "id": 11, "uuid": "env-prod", "name": "production" }, { "id": 12, "uuid": "env-staging", "name": "staging" }]
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects/proj-portfolio/environments")
            .Respond(HttpStatusCode.OK, "application/json", """
            [{ "id": 21, "uuid": "env-portfolio", "name": "production" }]
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications")
            .Respond(HttpStatusCode.OK, "application/json", """
            [
              { "uuid": "app-reelhub", "name": "reel-hub:feature/onboarding-wizard", "build_pack": "dockercompose",
                "status": "running:unknown", "git_repository": "https://github.com/acme/reelhub", "git_branch": "feature/onboarding-wizard",
                "fqdn": null, "environment_id": 11,
                "docker_compose_domains": { "web": { "name": "web", "domain": "https://reelhub.example.com" } } },
              { "uuid": "app-portfolio-api", "name": "portfolio-api", "build_pack": "dockerfile",
                "status": "exited:unhealthy", "git_repository": "https://github.com/acme/portfolio", "git_branch": "main",
                "fqdn": "https://api.example.com,http://pkyy.46.225.80.188.sslip.io", "environment_id": 21 },
              { "uuid": "app-orphan", "name": "orphan", "build_pack": "nixpacks", "status": "running:healthy",
                "git_repository": "https://github.com/acme/orphan", "git_branch": "main", "fqdn": null, "environment_id": 99 }
            ]
            """);
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases")
            .Respond(HttpStatusCode.OK, "application/json", """
            [
              { "uuid": "db-portfolio", "name": "portfolio-db", "database_type": "standalone-postgresql", "status": "running:healthy", "environment_id": 21 },
              { "uuid": "db-orphan", "name": "lost-db", "database_type": "standalone-redis", "status": "running:healthy", "environment_id": 98 }
            ]
            """);
        return handler;
    }

    [Fact]
    public async Task GetInventoryAsync_PlacesEveryResourceUnderItsProjectAndEnvironment()
    {
        var provider = CreateProvider(InstanceWithTwoProjects());

        var inventory = await provider.GetInventoryAsync(Credentials, CancellationToken.None);

        var nabdah = Assert.Single(inventory.Projects, p => p.Uuid == "proj-nabdah");
        Assert.False(nabdah.EnvironmentsInconclusive);
        Assert.Equal(["production", "staging"], nabdah.Environments.Select(e => e.Name));

        var reelhub = Assert.Single(nabdah.Environments.Single(e => e.Name == "production").Applications);
        Assert.Equal("dockercompose", reelhub.BuildPack);
        Assert.Equal("feature/onboarding-wizard", reelhub.GitBranch);
        Assert.Equal(["https://reelhub.example.com"], reelhub.Domains);
        Assert.Empty(nabdah.Environments.Single(e => e.Name == "staging").Applications);

        var portfolioEnv = Assert.Single(inventory.Projects.Single(p => p.Uuid == "proj-portfolio").Environments);
        var api = Assert.Single(portfolioEnv.Applications);
        Assert.Equal("exited:unhealthy", api.Status);
        Assert.Equal(["https://api.example.com", "http://pkyy.46.225.80.188.sslip.io"], api.Domains);
        var db = Assert.Single(portfolioEnv.Databases);
        Assert.Equal("standalone-postgresql", db.Engine);
    }

    /// <summary>An app that matches no environment is reported, not dropped: it still exists.</summary>
    [Fact]
    public async Task GetInventoryAsync_KeepsResourcesThatMatchNoEnvironment()
    {
        var provider = CreateProvider(InstanceWithTwoProjects());

        var inventory = await provider.GetInventoryAsync(Credentials, CancellationToken.None);

        Assert.Equal(["app-orphan"], inventory.UnplacedApplications.Select(a => a.Uuid));
        Assert.Equal(["db-orphan"], inventory.UnplacedDatabases.Select(d => d.Uuid));
    }

    /// <summary>
    /// A project whose environment listing fails must not look like a project with no
    /// environments — that is the absence rule, and here the difference is between "empty
    /// project" and "Coolify would not tell us".
    /// </summary>
    [Fact]
    public async Task GetInventoryAsync_MarksAProjectInconclusive_WhenItsEnvironmentsCannotBeListed()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "proj-a", "name": "alpha" }]""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects/proj-a/environments")
            .Respond(HttpStatusCode.InternalServerError, "application/json", """{ "message": "boom" }""");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases")
            .Respond(HttpStatusCode.OK, "application/json", "[]");

        var inventory = await CreateProvider(handler).GetInventoryAsync(Credentials, CancellationToken.None);

        var project = Assert.Single(inventory.Projects);
        Assert.True(project.EnvironmentsInconclusive);
        Assert.Empty(project.Environments);
    }

    [Fact]
    public async Task GetInventoryAsync_EmptyInstance_IsEmptyAndConclusive()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/projects")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications")
            .Respond(HttpStatusCode.OK, "application/json", "[]");
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/databases")
            .Respond(HttpStatusCode.OK, "application/json", "[]");

        var inventory = await CreateProvider(handler).GetInventoryAsync(Credentials, CancellationToken.None);

        Assert.Empty(inventory.Projects);
        Assert.Empty(inventory.UnplacedApplications);
        Assert.Empty(inventory.UnplacedDatabases);
    }
}
