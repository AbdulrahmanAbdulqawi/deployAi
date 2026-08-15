using System.Text.RegularExpressions;
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

        return
        [
            ..ComposeFileCandidates,
            $"{clientPrefix}Dockerfile",
            $"{clientPrefix}nginx.conf",
            $"{ServerBuildPrefix(serverPart)}Dockerfile",
            $"{ServerSourcePrefix(serverPart)}Controllers/HealthController.cs"
        ];
    }

    internal static IReadOnlyList<string> BuildAllScanPaths(
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart)
    {
        return
        [
            ..BuildReadinessFilePaths(websitePart, serverPart),
            $"{ServerSourcePrefix(serverPart)}Program.cs",
            "docs/DEPLOYMENT.md"
        ];
    }

    /// <summary>
    /// Where <c>docker build</c> actually runs for the api service — what compose's own
    /// <c>build:</c> context points at, and where its Dockerfile must sit.
    /// </summary>
    /// <remarks>
    /// Not the same directory as <see cref="ServerSourcePrefix"/> whenever the api's Dockerfile
    /// builds a nested project from a wider context — Mirqab's does: root-context multi-stage
    /// build, source three levels down at <c>src/Mirqab.Api</c>. Before <c>ServiceDirectory</c>
    /// could point somewhere other than the build root, using it here and for Program.cs/
    /// Controllers both happened to agree. Making ServiceDirectory answer "where is the source"
    /// correctly is what split them: the Dockerfile has to be looked for at the build root, or a
    /// deploy whose Dockerfile has always lived at the repository root gets told it is missing one.
    /// </remarks>
    private static string ServerBuildPrefix(DeploymentPlanPart serverPart) => Prefix(serverPart.RootDirectory);

    /// <summary>Where the server's own source lives — Program.cs, Controllers, appsettings.json.</summary>
    private static string ServerSourcePrefix(DeploymentPlanPart serverPart) =>
        Prefix(serverPart.ServiceDirectory ?? serverPart.RootDirectory);

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
        var webDockerfilePath = $"{clientPrefix}Dockerfile";
        var nginxPath = $"{clientPrefix}nginx.conf";
        var apiDockerfilePath = $"{ServerBuildPrefix(serverPart)}Dockerfile";
        var healthControllerPath = $"{ServerSourcePrefix(serverPart)}Controllers/HealthController.cs";
        var programPath = $"{ServerSourcePrefix(serverPart)}Program.cs";

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
        else
        {
            missing.AddRange(EvaluateWebDockerfile(webDockerfilePath, fileContentsByPath[webDockerfilePath]!));
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
        // Findings are reported against the file DeployAI can write, which is not always the file it
        // inspected. A repo's own docker-compose.yml is usually its local dev stack, so the remedy
        // for a rejected one is to *add* docker-compose.coolify.yml beside it — never to rewrite the
        // developer's environment. Naming the inspected path instead produced a required file no
        // generator could satisfy: nothing has a template called docker-compose.yml, the lookup
        // missed, and the one file the whole deployment depends on was dropped without a word.
        var reportedPath = ComposeFileName;
        var about = string.Equals(path, ComposeFileName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"`{path}` cannot be used as-is: ";

        if (!DeclaresService(content, "api") || !DeclaresService(content, "web"))
        {
            yield return new MissingDeploymentFile(
                reportedPath,
                $"{about}the compose file must declare both an `api` and a `web` service — nginx proxies to the api service by name.",
                DeploymentFileSeverity.Blocking);
        }

        if (PublishesHostPorts(content))
        {
            yield return new MissingDeploymentFile(
                reportedPath,
                $"{about}it publishes host ports. Coolify's Traefik terminates TLS and routes the domain itself; use `expose` on web instead.",
                DeploymentFileSeverity.Blocking);
        }

        if (!content.Contains("restart:", StringComparison.OrdinalIgnoreCase))
        {
            yield return new MissingDeploymentFile(
                reportedPath,
                $"{about}services should set `restart: unless-stopped` so they survive a host reboot.",
                DeploymentFileSeverity.Recommended);
        }
    }

    /// <summary>
    /// A web Dockerfile that exists is not necessarily this deployment's web Dockerfile.
    /// </summary>
    /// <remarks>
    /// Presence used to be the whole check, and presence is exactly what a Dockerfile written for a
    /// different shape has. Mirqab carried one generated by <c>SsrFrontendDockerfile</c> for a
    /// standalone website: nginx on 3000, its own config written inline with <c>printf</c>, and no
    /// <c>/api/</c> proxy — correct for a site deployed alone, wrong for a compose service. The
    /// compose run then emitted an nginx.conf that nothing copied, and the compose file exposed 80
    /// while the image listened on 3000. Every file individually passed; the deployment could not
    /// have served a request. Both symptoms had already been seen live on this app: 502 from the
    /// port mismatch, 405 on login from the absent proxy.
    /// </remarks>
    private static IEnumerable<MissingDeploymentFile> EvaluateWebDockerfile(string path, string content)
    {
        if (!content.Contains("nginx.conf", StringComparison.OrdinalIgnoreCase))
        {
            yield return new MissingDeploymentFile(
                path,
                "This Dockerfile never copies nginx.conf, so the /api proxy config never reaches the image. It was most likely generated for a standalone website, where there is no API to proxy to.",
                DeploymentFileSeverity.Blocking);
        }

        if (ExposesPortOtherThan80(content))
        {
            yield return new MissingDeploymentFile(
                path,
                "The compose file routes the domain to port 80 on the web service, and this image listens on a different port. The container runs, the deploy reports success, and every request gets a 502 from the proxy.",
                DeploymentFileSeverity.Blocking);
        }
    }

    /// <summary>
    /// Whether the image declares a port, and none of the ports it declares is 80. A Dockerfile
    /// with no EXPOSE at all is left alone — that is a different judgement, and guessing it wrong
    /// blocks a deploy that would have worked.
    /// </summary>
    private static bool ExposesPortOtherThan80(string content)
    {
        var exposed = Regex.Matches(content, @"^\s*EXPOSE\s+(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        return exposed.Length > 0 && !exposed.Contains("80");
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
