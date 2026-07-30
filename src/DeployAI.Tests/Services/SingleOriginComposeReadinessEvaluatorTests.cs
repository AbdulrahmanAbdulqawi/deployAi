using DeployAI.Api.Services;
using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

public class SingleOriginComposeReadinessEvaluatorTests
{
    private static readonly DeploymentPlanPart Website =
        new("website", "coolify", "client", null, null, null, null, null, "angular", null, null);

    private static readonly DeploymentPlanPart Server =
        new("server", "coolify", "src/api", "src/api", null, null, null, null, "dotnet", null, null);

    [Fact]
    public void Evaluate_CompleteRepo_IsReady()
    {
        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, BuildCompleteFiles());

        Assert.True(SingleOriginComposeReadinessEvaluator.IsReady(issues));
        Assert.DoesNotContain(issues, issue => issue.Severity == DeploymentFileSeverity.Blocking);
    }

    // The whole point of the separate evaluator: these files are correct to be absent, and the
    // split-origin rules would have blocked publishing over them.
    [Fact]
    public void Evaluate_DoesNotRequireSplitOriginScaffolding()
    {
        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, BuildCompleteFiles());

        Assert.DoesNotContain(issues, issue =>
            issue.Path.Contains("write-api-env", StringComparison.OrdinalIgnoreCase) ||
            issue.Path.Contains("api-base.interceptor", StringComparison.OrdinalIgnoreCase) ||
            issue.Path.Equals("railway.toml", StringComparison.OrdinalIgnoreCase) ||
            issue.Path.EndsWith("vercel.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_MissingComposeFile_Blocks()
    {
        var files = BuildCompleteFiles();
        files.Remove("docker-compose.coolify.yml");

        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, files);

        Assert.Contains(issues, issue =>
            issue.Path == "docker-compose.coolify.yml" &&
            issue.Severity == DeploymentFileSeverity.Blocking);
        Assert.False(SingleOriginComposeReadinessEvaluator.IsReady(issues));
    }

    // A repo's plain docker-compose.yml is usually the local dev stack, but if it is the only
    // compose file present we evaluate it rather than demanding a Coolify-specific name.
    [Fact]
    public void Evaluate_AcceptsPlainDockerComposeYml_WhenCoolifyFileAbsent()
    {
        var files = BuildCompleteFiles();
        files["docker-compose.yml"] = files["docker-compose.coolify.yml"];
        files.Remove("docker-compose.coolify.yml");

        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, files);

        Assert.True(SingleOriginComposeReadinessEvaluator.IsReady(issues));
    }

    // The finding has to name a file DeployAI can write. It named the file it inspected, and nothing
    // has a template called docker-compose.yml — so the lookup missed, the generator skipped it in
    // silence, and the PR arrived without the one file the deployment cannot proceed without.
    // Rewriting the repo's own docker-compose.yml would be wrong anyway: that is the dev stack.
    [Fact]
    public void Evaluate_RejectedDevComposeFile_AsksForTheCoolifyFileInstead()
    {
        var files = BuildCompleteFiles();
        files.Remove("docker-compose.coolify.yml");
        // Mirqab's actual file: a local dev stack that publishes host ports and names neither service.
        files["docker-compose.yml"] = """
            services:
              db:
                image: postgres:16
                ports:
                  - "5432:5432"
            """;

        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, files);

        Assert.Contains(issues, issue =>
            issue.Path == "docker-compose.coolify.yml" &&
            issue.Severity == DeploymentFileSeverity.Blocking);
        Assert.DoesNotContain(issues, issue => issue.Path == "docker-compose.yml");
        // And it still says which file it rejected, or the reason reads as if nothing was there.
        Assert.Contains(issues, issue => issue.Reason.Contains("docker-compose.yml", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ComposePublishingHostPorts_Blocks()
    {
        var files = BuildCompleteFiles();
        files["docker-compose.coolify.yml"] = """
            services:
              api:
                build: ./src/api
                restart: unless-stopped
              web:
                build: ./client
                ports:
                  - "80:80"
                restart: unless-stopped
            """;

        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, files);

        Assert.Contains(issues, issue =>
            issue.Path == "docker-compose.coolify.yml" &&
            issue.Severity == DeploymentFileSeverity.Blocking &&
            issue.Reason.Contains("host ports", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_NginxWithoutApiProxy_Blocks()
    {
        var files = BuildCompleteFiles();
        files["client/nginx.conf"] = """
            server {
              listen 80;
              client_max_body_size 25m;
              location / {
                try_files $uri $uri/ /index.html;
              }
            }
            """;

        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, files);

        Assert.Contains(issues, issue =>
            issue.Path == "client/nginx.conf" &&
            issue.Severity == DeploymentFileSeverity.Blocking &&
            issue.Reason.Contains("proxy_pass", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_NginxWithoutSpaFallback_Blocks()
    {
        var files = BuildCompleteFiles();
        files["client/nginx.conf"] = """
            server {
              listen 80;
              client_max_body_size 25m;
              location /api/ {
                proxy_pass http://api:8080;
              }
            }
            """;

        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, files);

        Assert.Contains(issues, issue =>
            issue.Path == "client/nginx.conf" &&
            issue.Severity == DeploymentFileSeverity.Blocking &&
            issue.Reason.Contains("SPA fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_MissingPerServiceDockerfiles_Blocks()
    {
        var files = BuildCompleteFiles();
        files.Remove("client/Dockerfile");
        files.Remove("src/api/Dockerfile");

        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, files);

        Assert.Contains(issues, issue => issue.Path == "client/Dockerfile" && issue.Severity == DeploymentFileSeverity.Blocking);
        Assert.Contains(issues, issue => issue.Path == "src/api/Dockerfile" && issue.Severity == DeploymentFileSeverity.Blocking);
    }

    // Missing health endpoint is worth flagging (verification uses it) but must not stop a deploy.
    [Fact]
    public void Evaluate_MissingHealthController_IsRecommendedNotBlocking()
    {
        var files = BuildCompleteFiles();
        files.Remove("src/api/Controllers/HealthController.cs");

        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, files);

        Assert.Contains(issues, issue =>
            issue.Path == "src/api/Controllers/HealthController.cs" &&
            issue.Severity == DeploymentFileSeverity.Recommended);
        Assert.True(SingleOriginComposeReadinessEvaluator.IsReady(issues));
    }

    [Fact]
    public void Evaluate_NginxWithoutBodySizeLimit_IsRecommendedNotBlocking()
    {
        var files = BuildCompleteFiles();
        files["client/nginx.conf"] = """
            server {
              listen 80;
              location /api/ {
                proxy_pass http://api:8080;
              }
              location / {
                try_files $uri $uri/ /index.html;
              }
            }
            """;

        var issues = SingleOriginComposeReadinessEvaluator.Evaluate(Website, Server, files);

        Assert.Contains(issues, issue =>
            issue.Path == "client/nginx.conf" &&
            issue.Severity == DeploymentFileSeverity.Recommended &&
            issue.Reason.Contains("client_max_body_size", StringComparison.OrdinalIgnoreCase));
        Assert.True(SingleOriginComposeReadinessEvaluator.IsReady(issues));
    }

    private static Dictionary<string, string?> BuildCompleteFiles() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["docker-compose.coolify.yml"] = """
            services:
              api:
                build: ./src/api
                volumes:
                  - appdata:/data
                restart: unless-stopped
              web:
                build: ./client
                expose:
                  - "80"
                depends_on:
                  - api
                restart: unless-stopped
            volumes:
              appdata:
            """,
        ["client/Dockerfile"] = "FROM node:22-alpine AS build",
        ["src/api/Dockerfile"] = "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build",
        ["client/nginx.conf"] = """
            server {
              listen 80;
              client_max_body_size 25m;
              location /api/ {
                proxy_pass http://api:8080;
              }
              location / {
                try_files $uri $uri/ /index.html;
              }
            }
            """,
        ["src/api/Controllers/HealthController.cs"] = "[Route(\"api/health\")] public class HealthController { }",
        ["src/api/Program.cs"] = "builder.Services.AddCors();",
        ["docs/DEPLOYMENT.md"] = "# Deployment"
    };
}
