using System.Net;
using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

internal sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(responder(request));
}

public class DeploymentEndpointProbesTests
{
    [Fact]
    public async Task CheckWebsiteHomepageAsync_FailsOnVercelNotFoundBody()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("NOT_FOUND")
        });
        var client = new HttpClient(handler);

        var result = await DeploymentEndpointProbes.CheckWebsiteHomepageAsync(
            client,
            "https://yemenicommunity.vercel.app",
            CancellationToken.None);

        Assert.Equal(ProbeCheckStatus.Failed, result.Status);
        Assert.Equal("fix_output_directory", result.SuggestedAction);
    }

    [Fact]
    public async Task CheckWebsiteHomepageAsync_PassedWhenHomepageLoads()
    {
        var client = new HttpClient(new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<!doctype html><app-root></app-root>", System.Text.Encoding.UTF8, "text/html")
            }));

        var result = await DeploymentEndpointProbes.CheckWebsiteHomepageAsync(
            client,
            "https://tickethub-sooty-five.vercel.app",
            CancellationToken.None);

        Assert.Equal(ProbeCheckStatus.Passed, result.Status);
        Assert.Contains("Website homepage loaded: https://tickethub-sooty-five.vercel.app/", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckSpaShell_PassedWhenAppRootAndScriptsPresent()
    {
        const string html = """<!doctype html><html><app-root></app-root><script src="main.js"></script></html>""";

        var result = DeploymentEndpointProbes.CheckSpaShell(html);

        Assert.Equal(ProbeCheckStatus.Passed, result.Status);
        Assert.Equal("SPA shell markers found in homepage HTML.", result.Message);
    }

    [Fact]
    public void CheckSpaShell_WarningWhenMissingShellMarkers()
    {
        var result = DeploymentEndpointProbes.CheckSpaShell("<html><body>Hello</body></html>");

        Assert.Equal(ProbeCheckStatus.Warning, result.Status);
        Assert.Contains("does not look like an SPA shell", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("fix_output_directory", result.SuggestedAction);
    }

    [Fact]
    public async Task CheckSplitOriginSpaWiringAsync_WarningWhenBundleMissingWiring()
    {
        const string homepage = """<!doctype html><app-root></app-root><script src="main.js"></script>""";
        const string bundle = """
            this.apiUrl="/api/v1/auth";
            this.http.post(`${this.apiUrl}/login`, credentials);
            """;

        var client = new HttpClient(new RecordingHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("main.js", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(bundle, System.Text.Encoding.UTF8, "application/javascript")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(homepage, System.Text.Encoding.UTF8, "text/html")
            };
        }));

        var result = await DeploymentEndpointProbes.CheckSplitOriginSpaWiringAsync(
            client,
            "https://tickethub-sooty-five.vercel.app",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(ProbeCheckStatus.Warning, result!.Status);
        Assert.Contains("missing split-origin wiring", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("apiBaseInterceptor", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("reconnect", result.SuggestedAction);
    }

    [Fact]
    public async Task CheckSplitOriginSpaWiringAsync_PassedWhenBundleWired()
    {
        const string homepage = """<!doctype html><app-root></app-root><script src="main.js"></script>""";
        const string bundle = """
            withInterceptors([apiBaseInterceptor]);
            apiBaseUrl:"https://api.example.com";
            this.apiUrl="/api/v1/auth";
            """;

        var client = new HttpClient(new RecordingHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("main.js", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(bundle, System.Text.Encoding.UTF8, "application/javascript")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(homepage, System.Text.Encoding.UTF8, "text/html")
            };
        }));

        var result = await DeploymentEndpointProbes.CheckSplitOriginSpaWiringAsync(
            client,
            "https://app.vercel.app",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(ProbeCheckStatus.Passed, result!.Status);
        Assert.Contains("Split-origin SPA bundle wiring detected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractScriptSources_FindsScriptAndModulepreloadLinks()
    {
        const string html = """
            <script src="main.js"></script>
            <link rel="modulepreload" href="chunk-abc.js">
            """;

        var sources = DeploymentEndpointProbes.ExtractScriptSources(html);

        Assert.Contains("main.js", sources);
        Assert.Contains("chunk-abc.js", sources);
    }
}

public class DeploymentVerificationServiceTests
{
    [Fact]
    public async Task VerifyAsync_WebsiteScope_SkipsServerChecksButRunsConnection()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: "https://app.vercel.app",
            serverUrl: "https://api.example.com");

        var handler = CreateSplitOriginHandler();
        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Website, CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Id == "website.reachable" && check.Status == "passed");
        Assert.DoesNotContain(result.Checks, check => check.Target == "server");
        Assert.Contains(result.Checks, check => check.Id == "connection.cors" && check.Target == "connection");
        Assert.Contains(result.Checks, check => check.Id == "connection.api_health" && check.Target == "connection");
    }

    [Fact]
    public async Task VerifyAsync_BothScope_RunsConnectionChecks()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: "https://app.vercel.app",
            serverUrl: "https://api.example.com");

        var handler = CreateSplitOriginHandler();
        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Both, CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Id == "connection.cors" && check.Status == "passed");
        Assert.Contains(result.Checks, check => check.Id == "connection.api_health" && check.Status == "passed");
        Assert.True(result.Success);
    }

    [Fact]
    public async Task VerifyAsync_ServerHealthFailure_SuggestsRedeploy()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: null,
            serverUrl: "https://api.example.com");

        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.ToString().Contains("/api/v1/health", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });

        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Server, CancellationToken.None);

        var health = Assert.Single(result.Checks, check => check.Id == "server.health");
        Assert.Equal("failed", health.Status);
        Assert.Equal("redeploy_server", health.SuggestedAction);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task VerifyAsync_WebsiteScope_ReturnsTicketHubLikeHealthChecks()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: "https://tickethub-sooty-five.vercel.app",
            serverUrl: "https://api.example.com");

        var handler = CreateTicketHubLikeHandler(wiredBundle: false);
        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Website, CancellationToken.None);

        var homepage = Assert.Single(result.Checks, check => check.Id == "website.reachable");
        Assert.Equal("passed", homepage.Status);
        Assert.Equal("Website homepage", homepage.Label);
        Assert.Contains("Website homepage loaded:", homepage.Message, StringComparison.Ordinal);
        Assert.Equal("https://tickethub-sooty-five.vercel.app/", homepage.Url);

        var spaShell = Assert.Single(result.Checks, check => check.Id == "website.spa_shell");
        Assert.Equal("passed", spaShell.Status);
        Assert.Equal("SPA shell", spaShell.Label);
        Assert.Equal("SPA shell markers found in homepage HTML.", spaShell.Message);

        var wiring = Assert.Single(result.Checks, check => check.Id == "website.split_origin_wiring");
        Assert.Equal("warning", wiring.Status);
        Assert.Equal("Split-origin bundle wiring", wiring.Label);
        Assert.Contains("missing split-origin wiring", wiring.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("apiBaseInterceptor", wiring.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(wiring.CanRequestClaudeFix);
        Assert.Equal("reconnect", wiring.SuggestedAction);

        Assert.Contains(result.Checks, check => check.Id == "connection.cors" && check.Target == "connection");
        Assert.Contains(result.Checks, check => check.Id == "connection.api_health" && check.Target == "connection");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task VerifyAsync_WebsiteScope_SplitOriginWiringPassedWhenBundleWired()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: "https://app.vercel.app",
            serverUrl: "https://api.example.com");

        var handler = CreateTicketHubLikeHandler(wiredBundle: true);
        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Website, CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Id == "website.reachable" &&
            check.Status == "passed" &&
            check.Label == "Website homepage");
        Assert.Contains(result.Checks, check =>
            check.Id == "website.spa_shell" &&
            check.Status == "passed" &&
            check.Label == "SPA shell");
        Assert.Contains(result.Checks, check =>
            check.Id == "website.split_origin_wiring" &&
            check.Status == "passed" &&
            check.Label == "Split-origin bundle wiring");
        Assert.True(result.Success);
    }

    [Fact]
    public async Task VerifyAsync_WebsiteScope_Homepage404_SkipsSpaAndWiringChecks()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: "https://app.vercel.app",
            serverUrl: "https://api.example.com");

        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("NOT_FOUND")
        });
        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Website, CancellationToken.None);

        var homepage = Assert.Single(result.Checks, check => check.Id == "website.reachable");
        Assert.Equal("failed", homepage.Status);
        Assert.Equal("fix_output_directory", homepage.SuggestedAction);
        Assert.True(homepage.CanRequestClaudeFix);
        Assert.DoesNotContain(result.Checks, check => check.Id == "website.spa_shell");
        Assert.DoesNotContain(result.Checks, check => check.Id == "website.split_origin_wiring");
    }

    [Fact]
    public async Task VerifyAsync_Connection_ResolvesLiveRailwayUrlWhenDeployUrlMissing()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: "https://app.vercel.app",
            serverUrl: null,
            includeServerTargetWithoutUrl: true,
            includeRailwayCredential: true);

        var railwayOperations = new Mock<IProviderServiceOperations>();
        railwayOperations.SetupGet(o => o.ProviderName).Returns("railway");
        railwayOperations
            .Setup(o => o.GetServiceStatusAsync(
                It.IsAny<ProviderCredentials>(),
                "svc_api",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderServiceStatus("ready", "https://api-live.example.com", DateTimeOffset.UtcNow));

        var handler = CreateSplitOriginHandler(apiOrigin: "https://api-live.example.com");
        var service = CreateService(db, handler, railwayOperations.Object);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Website, CancellationToken.None);

        var connection = Assert.Single(result.Checks, check => check.Id == "connection.api_health");
        Assert.Equal("passed", connection.Status);
        Assert.Contains("https://api-live.example.com/api/v1/health", connection.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_Connection_UsesLatestStoredApiUrlWhenCurrentDeployUrlMissing()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: "https://app.vercel.app",
            serverUrl: null,
            includeServerTargetWithoutUrl: true);

        var railwayTargetId = await db.Deployments
            .Where(d => d.Id == deploymentId)
            .SelectMany(d => d.Project.DeployTargets)
            .Where(t => t.ProviderName == "railway")
            .Select(t => t.Id)
            .SingleAsync();

        var projectId = await db.Deployments.Where(d => d.Id == deploymentId).Select(d => d.ProjectId).SingleAsync();
        var previousDeploymentId = Guid.NewGuid();
        db.Deployments.Add(new Deployment
        {
            Id = previousDeploymentId,
            ProjectId = projectId,
            Branch = "main",
            TriggeredBy = "user",
            Status = DeploymentStatuses.Success,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Targets =
            [
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = previousDeploymentId,
                    DeployTargetId = railwayTargetId,
                    ProviderName = "railway",
                    Status = DeploymentStatuses.Success,
                    DeployUrl = "https://api-stored.example.com",
                    CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
                }
            ]
        });
        await db.SaveChangesAsync();

        var handler = CreateSplitOriginHandler(apiOrigin: "https://api-stored.example.com");
        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Website, CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Id == "connection.cors" && check.Status == "passed");
        Assert.Contains(result.Checks, check => check.Id == "connection.api_health" && check.Status == "passed");
    }

    [Fact]
    public async Task VerifyAsync_Connection_SkipsWithApiSpecificMessageWhenApiUrlUnavailable()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: "https://app.vercel.app",
            serverUrl: null,
            includeServerTargetWithoutUrl: true);

        var handler = CreateSplitOriginHandler();
        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Website, CancellationToken.None);

        var skip = Assert.Single(result.Checks, check => check.Id == "connection.no_urls");
        Assert.Equal("skipped", skip.Status);
        Assert.Contains("public API URL", skip.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Railway", skip.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_Connection_SkipsWithWebsiteSpecificMessageWhenWebsiteUrlUnavailable()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: null,
            serverUrl: "https://api.example.com",
            includeWebsiteTargetWithoutUrl: true);

        var handler = CreateSplitOriginHandler();
        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Server, CancellationToken.None);

        var skip = Assert.Single(result.Checks, check => check.Id == "connection.no_urls");
        Assert.Equal("skipped", skip.Status);
        Assert.Contains("public website URL", skip.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Vercel", skip.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_ServerScope_IncludesConnectionWhenBothUrlsResolvable()
    {
        await using var db = CreateDb();
        var deploymentId = await SeedDeploymentAsync(
            db,
            websiteUrl: "https://app.vercel.app",
            serverUrl: "https://api.example.com");

        var handler = CreateSplitOriginHandler();
        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Server, CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Id == "server.reachable");
        Assert.Contains(result.Checks, check => check.Id == "connection.cors");
        Assert.DoesNotContain(result.Checks, check => check.Id == "website.reachable");
    }

    [Fact]
    public async Task VerifyAsync_BothScope_VerifiesBothPairs_WhenDeploymentHasVercelRailwayAndCoolifyPairs()
    {
        // Regression test: a deployment with two complete provider pairs used to have only the
        // first-matched pair verified (DeploymentTargetResolution.FindWebsiteTarget/FindServerTarget
        // picked one match across all providers), silently skipping the other pair's checks forever.
        await using var db = CreateDb();
        var deploymentId = await SeedMixedPairDeploymentAsync(db);

        var handler = CreateMixedPairHandler();
        var service = CreateService(db, handler);
        var result = await service.VerifyAsync(deploymentId, DeploymentVerificationScope.Both, CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Id == "vercel+railway:website.reachable" && check.Status == "passed");
        Assert.Contains(result.Checks, check => check.Id == "vercel+railway:connection.cors" && check.Status == "passed");
        Assert.Contains(result.Checks, check => check.Id == "coolify+coolify:website.reachable" && check.Status == "passed");
        Assert.Contains(result.Checks, check => check.Id == "coolify+coolify:connection.cors" && check.Status == "passed");
        Assert.True(result.Success);
    }

    private static RecordingHttpMessageHandler CreateSplitOriginHandler(string apiOrigin = "https://api.example.com")
    {
        return new RecordingHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("app.vercel.app", StringComparison.OrdinalIgnoreCase))
            {
                if (url.Contains("main.js", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $"withInterceptors([apiBaseInterceptor]); apiBaseUrl:'{apiOrigin}'; apiUrl='/api'",
                            System.Text.Encoding.UTF8,
                            "application/javascript")
                    };
                }

                return HtmlResponse("<!doctype html><app-root></app-root><script src=\"main.js\"></script>");
            }

            if (url.Contains("/api/v1/health", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"status":"healthy"}""");
            }

            if (request.Method == HttpMethod.Options)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("Access-Control-Allow-Origin", "https://app.vercel.app");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });
    }

    private static RecordingHttpMessageHandler CreateTicketHubLikeHandler(bool wiredBundle)
    {
        const string homepage = """<!doctype html><html><app-root></app-root><script src="main.js"></script></html>""";
        var bundle = wiredBundle
            ? """
              withInterceptors([apiBaseInterceptor]);
              apiBaseUrl:"https://api.example.com";
              this.apiUrl="/api/v1/auth";
              """
            : """
              this.apiUrl="/api/v1/auth";
              this.apiUrl="/api/Settings";
              this.http.post(`${this.apiUrl}/login`, credentials);
              """;

        return new RecordingHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("main.js", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(bundle, System.Text.Encoding.UTF8, "application/javascript")
                };
            }

            if (url.Contains("/api/v1/health", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"status":"healthy"}""");
            }

            if (request.Method == HttpMethod.Options)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("Access-Control-Allow-Origin", request.Headers.GetValues("Origin").FirstOrDefault() ?? "*");
                return response;
            }

            if (url.Contains("vercel.app", StringComparison.OrdinalIgnoreCase))
            {
                return HtmlResponse(homepage);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });
    }

    private static RecordingHttpMessageHandler CreateMixedPairHandler()
    {
        return new RecordingHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (request.Method == HttpMethod.Options)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("Access-Control-Allow-Origin", request.Headers.GetValues("Origin").FirstOrDefault() ?? "*");
                return response;
            }

            if (url.Contains("/api/v1/health", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"status":"healthy"}""");
            }

            if (url.Contains("main.js", StringComparison.OrdinalIgnoreCase))
            {
                var apiOrigin = url.Contains("coolify-web", StringComparison.OrdinalIgnoreCase)
                    ? "https://coolify-api.example.com"
                    : "https://api.example.com";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"withInterceptors([apiBaseInterceptor]); apiBaseUrl:'{apiOrigin}'; apiUrl='/api'",
                        System.Text.Encoding.UTF8,
                        "application/javascript")
                };
            }

            if (url.Contains("app.vercel.app", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("coolify-web.example.com", StringComparison.OrdinalIgnoreCase))
            {
                return HtmlResponse("<!doctype html><app-root></app-root><script src=\"main.js\"></script>");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });
    }

    private static async Task<Guid> SeedMixedPairDeploymentAsync(DeployAIDbContext db)
    {
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var vercelTargetId = Guid.NewGuid();
        var railwayTargetId = Guid.NewGuid();
        var coolifyWebsiteTargetId = Guid.NewGuid();
        var coolifyServerTargetId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();

        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "Mixed",
            GitHubRepoFullName = "tester/mixed",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = vercelTargetId,
                    ProjectId = projectId,
                    ProviderName = "vercel",
                    ProviderProjectId = "prj_web",
                    ConfigJson = """{"role":"website","framework":"angular"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = railwayTargetId,
                    ProjectId = projectId,
                    ProviderName = "railway",
                    ProviderProjectId = "svc_api",
                    ConfigJson = """{"role":"server","framework":"dotnet"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = coolifyWebsiteTargetId,
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    ProviderProjectId = "app_web",
                    ConfigJson = """{"role":"website","framework":"angular"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = coolifyServerTargetId,
                    ProjectId = projectId,
                    ProviderName = "coolify",
                    ProviderProjectId = "app_api",
                    ConfigJson = """{"role":"server","framework":"dotnet"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        db.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            ProjectId = projectId,
            Branch = "main",
            TriggeredBy = "user",
            Status = DeploymentStatuses.Success,
            CreatedAt = DateTimeOffset.UtcNow,
            Targets =
            [
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = deploymentId,
                    DeployTargetId = vercelTargetId,
                    ProviderName = "vercel",
                    Status = DeploymentStatuses.Success,
                    DeployUrl = "https://app.vercel.app",
                    CompletedAt = DateTimeOffset.UtcNow
                },
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = deploymentId,
                    DeployTargetId = railwayTargetId,
                    ProviderName = "railway",
                    Status = DeploymentStatuses.Success,
                    DeployUrl = "https://api.example.com",
                    CompletedAt = DateTimeOffset.UtcNow
                },
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = deploymentId,
                    DeployTargetId = coolifyWebsiteTargetId,
                    ProviderName = "coolify",
                    Status = DeploymentStatuses.Success,
                    DeployUrl = "https://coolify-web.example.com",
                    CompletedAt = DateTimeOffset.UtcNow
                },
                new DeploymentTarget
                {
                    Id = Guid.NewGuid(),
                    DeploymentId = deploymentId,
                    DeployTargetId = coolifyServerTargetId,
                    ProviderName = "coolify",
                    Status = DeploymentStatuses.Success,
                    DeployUrl = "https://coolify-api.example.com",
                    CompletedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        await db.SaveChangesAsync();
        return deploymentId;
    }

    private static DeploymentVerificationService CreateService(
        DeployAIDbContext db,
        HttpMessageHandler handler,
        IProviderServiceOperations? railwayOperations = null)
    {
        var managementFactory = new Mock<IProviderManagementFactory>();
        var serviceOperationsFactory = new Mock<IProviderServiceOperationsFactory>();
        serviceOperationsFactory
            .Setup(f => f.GetServiceOperations("railway"))
            .Returns(railwayOperations);
        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var httpClientFactory = new StubHttpClientFactory(handler);
        return new DeploymentVerificationService(
            db,
            managementFactory.Object,
            serviceOperationsFactory.Object,
            tokens.Object,
            httpClientFactory);
    }

    private static async Task<Guid> SeedDeploymentAsync(
        DeployAIDbContext db,
        string? websiteUrl,
        string? serverUrl,
        bool includeServerTargetWithoutUrl = false,
        bool includeWebsiteTargetWithoutUrl = false,
        bool includeRailwayCredential = false)
    {
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var vercelTargetId = Guid.NewGuid();
        var railwayTargetId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        ProviderCredential? railwayCredential = null;
        if (includeRailwayCredential)
        {
            railwayCredential = new ProviderCredential
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ProviderName = "railway",
                TokenEncrypted = [1],
                Label = "Railway",
                IsValid = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ProviderCredentials.Add(railwayCredential);
        }

        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "Demo",
            GitHubRepoFullName = "tester/demo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = vercelTargetId,
                    ProjectId = projectId,
                    ProviderName = "vercel",
                    ProviderProjectId = "prj_web",
                    ConfigJson = """{"role":"website","framework":"angular"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = railwayTargetId,
                    ProjectId = projectId,
                    ProviderName = "railway",
                    ProviderProjectId = "svc_api",
                    CredentialId = railwayCredential?.Id ?? Guid.Empty,
                    ConfigJson = """{"role":"server","framework":"dotnet"}""",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Credential = railwayCredential!
                }
            ]
        });

        var targets = new List<DeploymentTarget>();
        if (serverUrl is not null || includeServerTargetWithoutUrl)
        {
            targets.Add(new DeploymentTarget
            {
                Id = Guid.NewGuid(),
                DeploymentId = deploymentId,
                DeployTargetId = railwayTargetId,
                ProviderName = "railway",
                Status = DeploymentStatuses.Success,
                DeployUrl = serverUrl,
                CompletedAt = DateTimeOffset.UtcNow
            });
        }

        if (websiteUrl is not null || includeWebsiteTargetWithoutUrl)
        {
            targets.Add(new DeploymentTarget
            {
                Id = Guid.NewGuid(),
                DeploymentId = deploymentId,
                DeployTargetId = vercelTargetId,
                ProviderName = "vercel",
                Status = DeploymentStatuses.Success,
                DeployUrl = websiteUrl,
                CompletedAt = DateTimeOffset.UtcNow
            });
        }

        db.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            ProjectId = projectId,
            Branch = "main",
            TriggeredBy = "user",
            Status = DeploymentStatuses.Success,
            CreatedAt = DateTimeOffset.UtcNow,
            Targets = targets
        });

        await db.SaveChangesAsync();
        return deploymentId;
    }

    private static DeployAIDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeployAIDbContext(options);
    }

    private static HttpResponseMessage HtmlResponse(string html) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
        };

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
