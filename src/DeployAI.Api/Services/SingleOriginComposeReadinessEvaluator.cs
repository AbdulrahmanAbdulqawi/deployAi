using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

/// <summary>
/// Readiness rules for a <see cref="DeploymentPlanKind.CoolifyCompose"/> plan.
///
/// Deliberately not a variant of <see cref="SplitOriginReadinessEvaluator"/>: that one hard-requires
/// write-api-env.mjs and api-base.interceptor.ts, which a single-origin app must NOT have — the SPA
/// calls relative /api paths that nginx proxies in-network, so there is no API base URL to inject.
/// Running the split-origin rules against a compose repo would emit Blocking findings demanding
/// files the app is correct to omit, and DeploymentOrchestrator refuses to publish on Blocking.
/// </summary>
internal static class SingleOriginComposeReadinessEvaluator
{
    internal const string ComposeFileName = "docker-compose.coolify.yml";

    /// <summary>
    /// Fallback names, in priority order. The Coolify-specific file wins because a repo's plain
    /// docker-compose.yml is usually the local dev stack (host ports, bind mounts, a dev database)
    /// and deploying it would publish ports Traefik expects to own.
    /// </summary>
    private static readonly string[] ComposeFileCandidates =
    [
        ComposeFileName,
        "docker-compose.coolify.yaml",
        "docker-compose.yml",
        "docker-compose.yaml"
    ];

    internal static IReadOnlyList<string> BuildReadinessFilePaths(
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart)
    {
        var clientPrefix = Prefix(websitePart.RootDirectory);
        var serverPrefix = Prefix(serverPart.ServiceDirectory ?? serverPart.RootDirectory);

        return
        [
            ..ComposeFileCandidates,
            $"{clientPrefix}Dockerfile",
            $"{clientPrefix}nginx.conf",
            $"{serverPrefix}Dockerfile",
            $"{serverPrefix}Controllers/HealthController.cs"
        ];
    }

    internal static IReadOnlyList<string> BuildAllScanPaths(
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart)
    {
        var serverPrefix = Prefix(serverPart.ServiceDirectory ?? serverPart.RootDirectory);

        return
        [
            ..BuildReadinessFilePaths(websitePart, serverPart),
            $"{serverPrefix}Program.cs",
            "docs/DEPLOYMENT.md"
        ];
    }

    internal static IReadOnlyList<MissingDeploymentFile> BuildRegenerationTargets(
        IReadOnlyList<DeploymentPlanPart> parts)
    {
        var website = SplitOriginDetection.FindWebsitePart(parts);
        var server = SplitOriginDetection.FindServerPart(parts);
        if (website is null || server is null || !SplitOriginDetection.PlanUsesSingleOriginCompose(parts))
        {
            return [];
        }

        return BuildAllScanPaths(website, server)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new MissingDeploymentFile(
                path,
                "Regenerate single-origin compose deployment setup and verify wiring.",
                DeploymentFileSeverity.Recommended))
            .ToArray();
    }

    internal static IReadOnlyList<MissingDeploymentFile> Evaluate(
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart,
        IReadOnlyDictionary<string, string?> fileContentsByPath)
    {
        var missing = new List<MissingDeploymentFile>();
        var clientPrefix = Prefix(websitePart.RootDirectory);
        var serverPrefix = Prefix(serverPart.ServiceDirectory ?? serverPart.RootDirectory);
        var webDockerfilePath = $"{clientPrefix}Dockerfile";
        var nginxPath = $"{clientPrefix}nginx.conf";
        var apiDockerfilePath = $"{serverPrefix}Dockerfile";
        var healthControllerPath = $"{serverPrefix}Controllers/HealthController.cs";
        var programPath = $"{serverPrefix}Program.cs";

        var composePath = ComposeFileCandidates.FirstOrDefault(
            candidate => !IsMissing(fileContentsByPath, candidate));

        if (composePath is null)
        {
            missing.Add(new MissingDeploymentFile(
                ComposeFileName,
                "A Docker Compose file is required — it is the single resource Coolify deploys for this app.",
                DeploymentFileSeverity.Blocking));
        }
        else
        {
            missing.AddRange(EvaluateComposeFile(composePath, fileContentsByPath[composePath]!));
        }

        if (IsMissing(fileContentsByPath, webDockerfilePath))
        {
            missing.Add(new MissingDeploymentFile(
                webDockerfilePath,
                "The web service builds from this directory, so it needs its own Dockerfile.",
                DeploymentFileSeverity.Blocking));
        }

        if (IsMissing(fileContentsByPath, apiDockerfilePath))
        {
            missing.Add(new MissingDeploymentFile(
                apiDockerfilePath,
                "The api service builds from this directory, so it needs its own Dockerfile.",
                DeploymentFileSeverity.Blocking));
        }

        if (IsMissing(fileContentsByPath, nginxPath))
        {
            missing.Add(new MissingDeploymentFile(
                nginxPath,
                "nginx.conf is what makes this single-origin: it serves the SPA and proxies /api to the api service.",
                DeploymentFileSeverity.Blocking));
        }
        else
        {
            missing.AddRange(EvaluateNginxConf(nginxPath, fileContentsByPath[nginxPath]!));
        }

        if (IsMissing(fileContentsByPath, healthControllerPath))
        {
            missing.Add(new MissingDeploymentFile(
                healthControllerPath,
                "A health endpoint lets us confirm the API came up behind the proxy after deploy.",
                DeploymentFileSeverity.Recommended));
        }

        if (!IsMissing(fileContentsByPath, programPath) &&
            UsesWideOpenCors(fileContentsByPath[programPath]))
        {
            missing.Add(new MissingDeploymentFile(
                programPath,
                "Program.cs uses AllowAnyOrigin(). A single-origin app is same-origin for its own SPA, so CORS should be restricted to the site origin.",
                DeploymentFileSeverity.Recommended));
        }

        if (IsMissing(fileContentsByPath, "docs/DEPLOYMENT.md"))
        {
            missing.Add(new MissingDeploymentFile(
                "docs/DEPLOYMENT.md",
                "Document the compose env vars that must be set in Coolify.",
                DeploymentFileSeverity.Recommended));
        }

        return missing;
    }

    internal static bool IsReady(IReadOnlyList<MissingDeploymentFile> issues) =>
        issues.All(issue => issue.Severity != DeploymentFileSeverity.Blocking);

    private static IEnumerable<MissingDeploymentFile> EvaluateComposeFile(string path, string content)
    {
        if (!DeclaresService(content, "api") || !DeclaresService(content, "web"))
        {
            yield return new MissingDeploymentFile(
                path,
                "Compose file must declare both an `api` and a `web` service — nginx proxies to the api service by name.",
                DeploymentFileSeverity.Blocking);
        }

        if (PublishesHostPorts(content))
        {
            yield return new MissingDeploymentFile(
                path,
                "Compose file publishes host ports. Coolify's Traefik terminates TLS and routes the domain itself; use `expose` on web instead.",
                DeploymentFileSeverity.Blocking);
        }

        if (!content.Contains("restart:", StringComparison.OrdinalIgnoreCase))
        {
            yield return new MissingDeploymentFile(
                path,
                "Services should set `restart: unless-stopped` so they survive a host reboot.",
                DeploymentFileSeverity.Recommended);
        }
    }

    private static IEnumerable<MissingDeploymentFile> EvaluateNginxConf(string path, string content)
    {
        if (!content.Contains("proxy_pass", StringComparison.OrdinalIgnoreCase) ||
            !content.Contains("/api", StringComparison.OrdinalIgnoreCase))
        {
            yield return new MissingDeploymentFile(
                path,
                "nginx.conf must proxy_pass /api/ to the api service, or the SPA's relative API calls will 404 against the static bundle.",
                DeploymentFileSeverity.Blocking);
        }

        if (!content.Contains("try_files", StringComparison.OrdinalIgnoreCase) ||
            !content.Contains("index.html", StringComparison.OrdinalIgnoreCase))
        {
            yield return new MissingDeploymentFile(
                path,
                "nginx.conf needs a SPA fallback (`try_files $uri $uri/ /index.html`) or deep links will 404 on refresh.",
                DeploymentFileSeverity.Blocking);
        }

        if (!content.Contains("client_max_body_size", StringComparison.OrdinalIgnoreCase))
        {
            yield return new MissingDeploymentFile(
                path,
                "nginx defaults to a 1 MB request body. Set client_max_body_size to match the API's own upload limit.",
                DeploymentFileSeverity.Recommended);
        }
    }

    /// <summary>
    /// A top-level `ports:` mapping in compose. Matched loosely rather than by parsing YAML,
    /// consistent with how the split-origin evaluator inspects file contents.
    /// </summary>
    private static bool PublishesHostPorts(string content) =>
        content
            .Split('\n')
            .Any(line => line.TrimStart().StartsWith("ports:", StringComparison.OrdinalIgnoreCase));

    private static bool DeclaresService(string content, string serviceName) =>
        content
            .Split('\n')
            .Any(line => line.TrimEnd().TrimEnd(':').Trim().Equals(serviceName, StringComparison.OrdinalIgnoreCase) &&
                         line.TrimEnd().EndsWith(":", StringComparison.Ordinal));

    private static bool UsesWideOpenCors(string? programCs) =>
        !string.IsNullOrWhiteSpace(programCs) &&
        programCs.Contains("AllowAnyOrigin()", StringComparison.Ordinal);

    private static bool IsMissing(IReadOnlyDictionary<string, string?> files, string path) =>
        !files.TryGetValue(path, out var content) || string.IsNullOrWhiteSpace(content);

    private static string Prefix(string? root)
    {
        var normalized = root?.Trim().Trim('/') ?? string.Empty;
        return string.IsNullOrEmpty(normalized) ? string.Empty : $"{normalized}/";
    }
}
