using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

/// <summary>
/// The actual readiness rule set for split-origin (Angular + .NET) deployments: which files must
/// exist, and beyond that, whether their *content* actually wires things up correctly (interceptor
/// registered, CORS configured, no legacy proxy rewrites, auth cookie/route conventions followed).
/// </summary>
internal static class SplitOriginReadinessEvaluator
{
    /// <summary>Lists every split-origin file as a "Recommended" regeneration target, for force-regenerating a repo's setup files regardless of current state.</summary>
    internal static IReadOnlyList<MissingDeploymentFile> BuildRegenerationTargets(
        IReadOnlyList<DeploymentPlanPart> parts)
    {
        var website = SplitOriginDetection.FindWebsitePart(parts);
        var server = SplitOriginDetection.FindServerPart(parts);
        if (website is null || server is null || !SplitOriginDetection.PlanUsesSplitOrigin(parts))
        {
            return [];
        }

        return BuildAllScanPaths(website, server)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new MissingDeploymentFile(
                path,
                "Regenerate split-origin deployment setup and verify wiring.",
                DeploymentFileSeverity.Recommended))
            .ToArray();
    }

    /// <summary>Lists every file path that needs to be fetched from GitHub to fully evaluate split-origin readiness.</summary>
    internal static IReadOnlyList<string> BuildAllScanPaths(DeploymentPlanPart websitePart, DeploymentPlanPart serverPart)
    {
        var clientRoot = NormalizeRoot(websitePart.RootDirectory);
        var serverRoot = NormalizeRoot(serverPart.ServiceDirectory ?? serverPart.RootDirectory);
        var clientPrefix = string.IsNullOrEmpty(clientRoot) ? string.Empty : $"{clientRoot}/";

        return
        [
            ..SplitOriginDetection.BuildReadinessFilePaths(websitePart, serverPart),
            $"{clientPrefix}angular.json",
            $"{clientPrefix}src/environments/environment.ts",
            $"{clientPrefix}src/environments/environment.production.ts",
            $"{clientPrefix}src/app/app.config.ts",
            $"{clientPrefix}src/app/core/services/auth.service.ts",
            $"{clientPrefix}src/app/core/services/signalr.service.ts",
            $"{serverRoot}/Program.cs",
            $"{serverRoot}/Controllers/AuthController.cs",
            "docs/DEPLOYMENT.md"
        ];
    }

    /// <summary>
    /// Evaluates a repo's fetched file contents against every split-origin wiring rule: required
    /// files present (Blocking if missing), interceptor registered, no proxy-rewrite anti-pattern,
    /// auth route/cookie conventions, CORS setup, and a handful of Recommended-severity best
    /// practices (health endpoint route, docs, withCredentials, absolute hub URL).
    /// </summary>
    internal static IReadOnlyList<MissingDeploymentFile> Evaluate(
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart,
        IReadOnlyDictionary<string, string?> fileContentsByPath)
    {
        var missing = new List<MissingDeploymentFile>();
        var coolifyStack = SplitOriginDetection.IsCoolifyFullStack(websitePart.ProviderName, serverPart.ProviderName);
        var stackLabel = coolifyStack ? "Coolify full-stack" : "Angular + Railway split-origin";
        var clientRoot = NormalizeRoot(websitePart.RootDirectory);
        var clientPrefix = string.IsNullOrEmpty(clientRoot) ? string.Empty : $"{clientRoot}/";
        var serverRoot = NormalizeRoot(serverPart.ServiceDirectory ?? serverPart.RootDirectory);
        var vercelPath = $"{clientPrefix}vercel.json";
        var angularPath = $"{clientPrefix}angular.json";
        var environmentPath = $"{clientPrefix}src/environments/environment.ts";
        var environmentProductionPath = $"{clientPrefix}src/environments/environment.production.ts";
        var writeApiEnvPath = $"{clientPrefix}scripts/write-api-env.mjs";
        var appConfigPath = $"{clientPrefix}src/app/app.config.ts";
        var interceptorPath = $"{clientPrefix}src/app/core/interceptors/api-base.interceptor.ts";
        var programPath = $"{serverRoot}/Program.cs";
        var authControllerPath = $"{serverRoot}/Controllers/AuthController.cs";
        var authServicePath = $"{clientPrefix}src/app/core/services/auth.service.ts";
        var signalrServicePath = $"{clientPrefix}src/app/core/services/signalr.service.ts";
        var serviceContents = SplitOriginClientWiringAnalyzer.SelectServiceFileContents(fileContentsByPath).ToArray();
        var registersInterceptor = SplitOriginClientWiringAnalyzer.RegistersApiBaseInterceptor(
            fileContentsByPath.GetValueOrDefault(appConfigPath));

        foreach (var path in SplitOriginDetection.BuildReadinessFilePaths(websitePart, serverPart))
        {
            if (IsMissing(fileContentsByPath, path))
            {
                missing.Add(new MissingDeploymentFile(
                    path,
                    $"Required for {stackLabel} deployment.",
                    DeploymentFileSeverity.Blocking));
            }
        }

        if (IsMissing(fileContentsByPath, angularPath))
        {
            missing.Add(new MissingDeploymentFile(
                angularPath,
                "angular.json is required for build configuration and environment file replacements.",
                DeploymentFileSeverity.Blocking));
        }
        else if (!SplitOriginClientWiringAnalyzer.HasAngularProductionFileReplacements(fileContentsByPath[angularPath]))
        {
            missing.Add(new MissingDeploymentFile(
                angularPath,
                "angular.json must use production fileReplacements for environment.production.ts so write-api-env output is bundled.",
                DeploymentFileSeverity.Blocking));
        }

        if (IsMissing(fileContentsByPath, appConfigPath))
        {
            missing.Add(new MissingDeploymentFile(
                appConfigPath,
                "app.config.ts must register apiBaseInterceptor before other HTTP interceptors.",
                DeploymentFileSeverity.Blocking));
        }
        else if (!registersInterceptor)
        {
            missing.Add(new MissingDeploymentFile(
                appConfigPath,
                "app.config.ts does not register apiBaseInterceptor. Relative /api requests will hit the Vercel SPA and return 405.",
                DeploymentFileSeverity.Blocking));
        }

        if (!registersInterceptor &&
            SplitOriginClientWiringAnalyzer.HasRelativeApiServicePaths(serviceContents))
        {
            missing.Add(new MissingDeploymentFile(
                interceptorPath,
                "Services use relative /api paths but apiBaseInterceptor is not registered in app.config.ts.",
                DeploymentFileSeverity.Blocking));
        }

        if (!coolifyStack &&
            fileContentsByPath.TryGetValue(vercelPath, out var vercelJson) &&
            !string.IsNullOrWhiteSpace(vercelJson) &&
            VercelJsonRewrites.HasApiProxyAntiPattern(vercelJson))
        {
            missing.Add(new MissingDeploymentFile(
                vercelPath,
                "vercel.json contains /api or /hubs proxy rewrites. Split-origin apps must call Railway directly.",
                DeploymentFileSeverity.Blocking));
        }

        if (!IsMissing(fileContentsByPath, authServicePath) &&
            SplitOriginClientWiringAnalyzer.HasAuthRouteMismatch(
                fileContentsByPath[authServicePath],
                fileContentsByPath.GetValueOrDefault(authControllerPath)))
        {
            missing.Add(new MissingDeploymentFile(
                authServicePath,
                "Auth service uses /api/Auth but the server route is api/v1/auth. Update the client path to /api/v1/auth.",
                DeploymentFileSeverity.Blocking));
        }

        if (!IsMissing(fileContentsByPath, programPath) &&
            HasDevOnlyCorsPolicy(fileContentsByPath[programPath]))
        {
            missing.Add(new MissingDeploymentFile(
                programPath,
                "Program.cs uses AllowAnyOrigin() or an \"AllowAll\" CORS policy. Split-origin apps need AllowedOrigins with AllowCredentials().",
                DeploymentFileSeverity.Recommended));
        }
        else if (!IsMissing(fileContentsByPath, programPath) &&
                 !HasSplitOriginCorsSetup(fileContentsByPath[programPath], coolifyStack))
        {
            missing.Add(new MissingDeploymentFile(
                programPath,
                coolifyStack
                    ? "Program.cs should configure AllowedOrigins and allow the Coolify website URL (FRONTEND_URL / App__FrontendUrl)."
                    : "Program.cs should configure AllowedOrigins and allow Vercel preview domains (*.vercel.app).",
                DeploymentFileSeverity.Recommended));
        }
        else if (IsMissing(fileContentsByPath, programPath))
        {
            missing.Add(new MissingDeploymentFile(
                programPath,
                "Program.cs should configure forwarded headers and CORS for split-origin deployment.",
                DeploymentFileSeverity.Recommended));
        }

        if (!IsMissing(fileContentsByPath, writeApiEnvPath) &&
            (IsMissing(fileContentsByPath, environmentPath) || IsMissing(fileContentsByPath, environmentProductionPath)))
        {
            var missingEnvironmentPath = IsMissing(fileContentsByPath, environmentPath)
                ? environmentPath
                : environmentProductionPath;
            missing.Add(new MissingDeploymentFile(
                missingEnvironmentPath,
                "Environment files are required when write-api-env.mjs injects apiBaseUrl at build time.",
                DeploymentFileSeverity.Recommended));
        }

        if (SplitOriginClientWiringAnalyzer.UsesLegacyApiUrlPropertyWithoutApiBaseUrl(
                [fileContentsByPath.GetValueOrDefault(environmentPath), fileContentsByPath.GetValueOrDefault(environmentProductionPath)],
                serviceContents))
        {
            missing.Add(new MissingDeploymentFile(
                environmentPath,
                "Services reference environment.apiUrl but environment files only define apiBaseUrl.",
                DeploymentFileSeverity.Recommended));
        }

        var healthControllerPath = $"{serverRoot}/Controllers/HealthController.cs";
        if (!IsMissing(fileContentsByPath, healthControllerPath) &&
            !HasHealthEndpointRoute(fileContentsByPath[healthControllerPath]))
        {
            missing.Add(new MissingDeploymentFile(
                healthControllerPath,
                "HealthController should expose GET api/v1/health for Railway health checks.",
                DeploymentFileSeverity.Recommended));
        }

        if (!IsMissing(fileContentsByPath, authControllerPath) &&
            !HasProductionSameSiteNoneCookie(fileContentsByPath[authControllerPath]))
        {
            missing.Add(new MissingDeploymentFile(
                authControllerPath,
                "AuthController should use SameSite=None and Secure for refresh cookies in Production.",
                DeploymentFileSeverity.Recommended));
        }

        if (IsMissing(fileContentsByPath, "docs/DEPLOYMENT.md"))
        {
            missing.Add(new MissingDeploymentFile(
                "docs/DEPLOYMENT.md",
                coolifyStack
                    ? "Document Coolify full-stack env var wiring for website and API apps."
                    : "Document split-origin env var wiring for Vercel and Railway.",
                DeploymentFileSeverity.Recommended));
        }

        if (!IsMissing(fileContentsByPath, authServicePath) &&
            !HasWithCredentials(fileContentsByPath[authServicePath]))
        {
            missing.Add(new MissingDeploymentFile(
                authServicePath,
                "Auth service should send withCredentials: true on login, refresh, and logout requests.",
                DeploymentFileSeverity.Recommended));
        }

        if (!IsMissing(fileContentsByPath, signalrServicePath) &&
            !HasAbsoluteHubUrl(fileContentsByPath[signalrServicePath]))
        {
            missing.Add(new MissingDeploymentFile(
                signalrServicePath,
                "SignalR service should use an absolute hub URL in production.",
                DeploymentFileSeverity.Recommended));
        }

        return missing;
    }

    /// <summary>A repo/plan is ready if none of its issues are Blocking severity - Recommended/Warning issues don't block readiness.</summary>
    internal static bool IsReady(IReadOnlyList<MissingDeploymentFile> issues) =>
        issues.All(issue => issue.Severity != DeploymentFileSeverity.Blocking);

    private static bool IsMissing(IReadOnlyDictionary<string, string?> files, string path) =>
        !files.TryGetValue(path, out var content) || string.IsNullOrWhiteSpace(content);

    private static bool HasDevOnlyCorsPolicy(string? programCs)
    {
        if (string.IsNullOrWhiteSpace(programCs))
        {
            return false;
        }

        return programCs.Contains("AllowAnyOrigin()", StringComparison.Ordinal) ||
               programCs.Contains("\"AllowAll\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasHealthEndpointRoute(string? healthController)
    {
        if (string.IsNullOrWhiteSpace(healthController))
        {
            return false;
        }

        return healthController.Contains("api/v1/health", StringComparison.OrdinalIgnoreCase) ||
               healthController.Contains("[Route(\"api/v1/health\")]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSplitOriginCorsSetup(string? programCs, bool coolifyStack = false)
    {
        if (string.IsNullOrWhiteSpace(programCs))
        {
            return false;
        }

        if (coolifyStack)
        {
            return programCs.Contains("AllowedOrigins", StringComparison.OrdinalIgnoreCase) ||
                   programCs.Contains("FRONTEND_URL", StringComparison.OrdinalIgnoreCase) ||
                   programCs.Contains("App__FrontendUrl", StringComparison.OrdinalIgnoreCase) ||
                   programCs.Contains("App__BaseUrl", StringComparison.OrdinalIgnoreCase);
        }

        return programCs.Contains("AllowedOrigins", StringComparison.OrdinalIgnoreCase) &&
               (programCs.Contains("SetIsOriginAllowed", StringComparison.OrdinalIgnoreCase) ||
                programCs.Contains("vercel.app", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasProductionSameSiteNoneCookie(string? authController)
    {
        if (string.IsNullOrWhiteSpace(authController))
        {
            return false;
        }

        return authController.Contains("SameSiteMode.None", StringComparison.OrdinalIgnoreCase) ||
               authController.Contains("SameSite = SameSiteMode.None", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasWithCredentials(string? authService)
    {
        if (string.IsNullOrWhiteSpace(authService))
        {
            return false;
        }

        return authService.Contains("withCredentials", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAbsoluteHubUrl(string? signalrService)
    {
        if (string.IsNullOrWhiteSpace(signalrService))
        {
            return false;
        }

        return signalrService.Contains("apiBaseUrl", StringComparison.OrdinalIgnoreCase) ||
               signalrService.Contains("environment.apiBaseUrl", StringComparison.OrdinalIgnoreCase) ||
               signalrService.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string? root) =>
        root?.Trim().Trim('/') ?? string.Empty;
}
