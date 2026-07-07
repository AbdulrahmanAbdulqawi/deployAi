using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

internal static class SplitOriginReadinessEvaluator
{
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

    internal static IReadOnlyList<MissingDeploymentFile> Evaluate(
        DeploymentPlanPart websitePart,
        DeploymentPlanPart serverPart,
        IReadOnlyDictionary<string, string?> fileContentsByPath)
    {
        var missing = new List<MissingDeploymentFile>();
        var clientRoot = NormalizeRoot(websitePart.RootDirectory);
        var clientPrefix = string.IsNullOrEmpty(clientRoot) ? string.Empty : $"{clientRoot}/";
        var serverRoot = NormalizeRoot(serverPart.ServiceDirectory ?? serverPart.RootDirectory);
        var vercelPath = $"{clientPrefix}vercel.json";
        var angularPath = $"{clientPrefix}angular.json";
        var environmentPath = $"{clientPrefix}src/environments/environment.ts";
        var environmentProductionPath = $"{clientPrefix}src/environments/environment.production.ts";
        var writeApiEnvPath = $"{clientPrefix}scripts/write-api-env.mjs";
        var appConfigPath = $"{clientPrefix}src/app/app.config.ts";
        var programPath = $"{serverRoot}/Program.cs";
        var authControllerPath = $"{serverRoot}/Controllers/AuthController.cs";
        var authServicePath = $"{clientPrefix}src/app/core/services/auth.service.ts";
        var signalrServicePath = $"{clientPrefix}src/app/core/services/signalr.service.ts";

        foreach (var path in SplitOriginDetection.BuildReadinessFilePaths(websitePart, serverPart))
        {
            if (IsMissing(fileContentsByPath, path))
            {
                missing.Add(new MissingDeploymentFile(
                    path,
                    "Required for Angular + Railway split-origin deployment.",
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
        else if (!HasAngularBuildHook(fileContentsByPath[angularPath], fileContentsByPath.GetValueOrDefault(vercelPath)))
        {
            missing.Add(new MissingDeploymentFile(
                angularPath,
                "angular.json should use fileReplacements for environment.production.ts or invoke write-api-env.mjs in the build.",
                DeploymentFileSeverity.Blocking));
        }

        if (IsMissing(fileContentsByPath, appConfigPath))
        {
            missing.Add(new MissingDeploymentFile(
                appConfigPath,
                "app.config.ts must register apiBaseInterceptor before other HTTP interceptors.",
                DeploymentFileSeverity.Blocking));
        }
        else if (!RegistersApiBaseInterceptor(fileContentsByPath[appConfigPath]))
        {
            missing.Add(new MissingDeploymentFile(
                appConfigPath,
                "app.config.ts does not register apiBaseInterceptor.",
                DeploymentFileSeverity.Blocking));
        }

        if (fileContentsByPath.TryGetValue(vercelPath, out var vercelJson) &&
            !string.IsNullOrWhiteSpace(vercelJson) &&
            VercelJsonRewrites.HasApiProxyAntiPattern(vercelJson))
        {
            missing.Add(new MissingDeploymentFile(
                vercelPath,
                "vercel.json contains /api or /hubs proxy rewrites. Split-origin apps must call Railway directly.",
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
            !HasSplitOriginCorsSetup(fileContentsByPath[programPath]))
        {
            missing.Add(new MissingDeploymentFile(
                programPath,
                "Program.cs should configure AllowedOrigins and allow Vercel preview domains (*.vercel.app).",
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
                "Document split-origin env var wiring for Vercel and Railway.",
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

    internal static bool IsReady(IReadOnlyList<MissingDeploymentFile> issues) =>
        issues.All(issue => issue.Severity != DeploymentFileSeverity.Blocking);

    private static bool IsMissing(IReadOnlyDictionary<string, string?> files, string path) =>
        !files.TryGetValue(path, out var content) || string.IsNullOrWhiteSpace(content);

    private static bool HasAngularBuildHook(string? angularJson, string? vercelJson)
    {
        if (!string.IsNullOrWhiteSpace(angularJson) &&
            angularJson.Contains("fileReplacements", StringComparison.OrdinalIgnoreCase) &&
            angularJson.Contains("environment.production", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(vercelJson) &&
               vercelJson.Contains("write-api-env", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RegistersApiBaseInterceptor(string? appConfig)
    {
        if (string.IsNullOrWhiteSpace(appConfig))
        {
            return false;
        }

        return appConfig.Contains("apiBaseInterceptor", StringComparison.OrdinalIgnoreCase) ||
               appConfig.Contains("api-base.interceptor", StringComparison.OrdinalIgnoreCase);
    }

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

    private static bool HasSplitOriginCorsSetup(string? programCs)
    {
        if (string.IsNullOrWhiteSpace(programCs))
        {
            return false;
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
