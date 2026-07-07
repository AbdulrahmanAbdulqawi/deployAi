using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

internal static class DeploymentFileScaffolder
{
    internal static IReadOnlyList<string> PatchablePaths { get; } =
    [
        "Program.cs",
        "Controllers/AuthController.cs"
    ];

    internal static bool RequiresExistingContent(string path)
    {
        var fileName = path.Replace('\\', '/').TrimStart('/').Split('/').LastOrDefault() ?? path;
        return fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("AuthController.cs", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<GeneratedDeploymentFile> ScaffoldMissingFiles(
        IReadOnlyList<DeploymentPlanPart> parts,
        IReadOnlyList<MissingDeploymentFile> missingFiles,
        IReadOnlyDictionary<string, string?>? existingFilesByPath = null)
    {
        var website = SplitOriginDetection.FindWebsitePart(parts);
        var server = SplitOriginDetection.FindServerPart(parts);
        if (website is null || server is null)
        {
            return [];
        }

        var generated = new List<GeneratedDeploymentFile>();
        foreach (var missing in missingFiles)
        {
            if (missing.Severity is not DeploymentFileSeverity.Blocking and not DeploymentFileSeverity.Recommended)
            {
                continue;
            }

            string? existing = null;
            existingFilesByPath?.TryGetValue(missing.Path, out existing);
            var content = TryGenerateFileContent(missing.Path, website, server, existing);
            if (!string.IsNullOrWhiteSpace(content))
            {
                generated.Add(new GeneratedDeploymentFile(missing.Path, content));
            }
        }

        return generated;
    }

    private static string? TryGenerateFileContent(
        string path,
        DeploymentPlanPart website,
        DeploymentPlanPart server,
        string? existingContent)
    {
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        var clientRoot = NormalizeRoot(website.RootDirectory);
        var clientPrefix = string.IsNullOrEmpty(clientRoot) ? string.Empty : $"{clientRoot}/";
        var serverRoot = NormalizeRoot(server.ServiceDirectory ?? server.RootDirectory);
        var dockerfilePath = server.DockerfilePath?.Replace('\\', '/').TrimStart('/') ??
                             $"{serverRoot}/Dockerfile";
        var outputDirectory = website.OutputDirectory?.Trim().Trim('/') ?? "dist/app/browser";
        var projectName = GuessProjectName(clientRoot);

        if (normalizedPath.Equals("railway.toml", StringComparison.OrdinalIgnoreCase))
        {
            return $"""
                [build]
                builder = "DOCKERFILE"
                dockerfilePath = "{dockerfilePath}"

                [deploy]
                restartPolicyType = "ON_FAILURE"
                """;
        }

        if (normalizedPath.Equals($"{clientPrefix}vercel.json", StringComparison.OrdinalIgnoreCase))
        {
            var buildCommand = string.IsNullOrWhiteSpace(website.BuildCommand)
                ? "npm ci && node scripts/write-api-env.mjs && npm run build"
                : website.BuildCommand.Contains("write-api-env", StringComparison.OrdinalIgnoreCase)
                    ? website.BuildCommand
                    : $"npm ci && node scripts/write-api-env.mjs && {website.BuildCommand}";

            return $$"""
                {
                  "version": 2,
                  "buildCommand": "{{buildCommand}}",
                  "outputDirectory": "{{outputDirectory}}",
                  "framework": null,
                  "rewrites": [
                    { "source": "/(.*)", "destination": "/index.html" }
                  ]
                }
                """;
        }

        if (normalizedPath.Equals($"{clientPrefix}scripts/write-api-env.mjs", StringComparison.OrdinalIgnoreCase))
        {
            return """
                import { writeFileSync } from 'node:fs';
                import { dirname, join } from 'node:path';
                import { fileURLToPath } from 'node:url';

                const apiUrl = (process.env.IDAARA_API_URL ?? process.env.NG_APP_API_URL ?? process.env.API_URL ?? '')
                  .trim()
                  .replace(/\/$/, '');

                if (!apiUrl && (process.env.VERCEL === '1' || process.env.CI === 'true')) {
                  console.error('Missing IDAARA_API_URL / NG_APP_API_URL for production build. API requests will hit the Vercel SPA and fail with 405.');
                  process.exit(1);
                }

                const __dirname = dirname(fileURLToPath(import.meta.url));
                const target = join(__dirname, '..', 'src', 'environments', 'environment.production.ts');
                const content = `export const environment = {\n  production: true,\n  apiBaseUrl: '${apiUrl}'\n};\n`;
                writeFileSync(target, content, 'utf8');
                console.log('Wrote production environment with apiBaseUrl:', apiUrl || '(empty)');
                """;
        }

        if (normalizedPath.EndsWith("api-base.interceptor.ts", StringComparison.OrdinalIgnoreCase))
        {
            return """
                import { HttpInterceptorFn } from '@angular/common/http';
                import { environment } from '../../../environments/environment';

                export const apiBaseInterceptor: HttpInterceptorFn = (req, next) => {
                  const base = environment.apiBaseUrl?.replace(/\/$/, '') ?? '';
                  if (!base) {
                    return next(req);
                  }

                  const path = req.url.split('?')[0];
                  if (/^\/api\//i.test(path) || /^\/hubs\//i.test(path)) {
                    return next(req.clone({ url: `${base}${req.url}` }));
                  }

                  return next(req);
                };
                """;
        }

        if (normalizedPath.EndsWith("HealthController.cs", StringComparison.OrdinalIgnoreCase))
        {
            return """
                using Microsoft.AspNetCore.Mvc;

                namespace DeployAI.Generated;

                [ApiController]
                [Route("api/v1/health")]
                public sealed class HealthController : ControllerBase
                {
                    [HttpGet]
                    public IActionResult Get() => Ok(new { status = "healthy" });
                }
                """;
        }

        if (normalizedPath.Equals($"{clientPrefix}angular.json", StringComparison.OrdinalIgnoreCase))
        {
            return $$"""
                {
                  "$schema": "./node_modules/@angular/cli/lib/config/schema.json",
                  "version": 1,
                  "projects": {
                    "{{projectName}}": {
                      "architect": {
                        "build": {
                          "configurations": {
                            "production": {
                              "fileReplacements": [
                                {
                                  "replace": "src/environments/environment.ts",
                                  "with": "src/environments/environment.production.ts"
                                }
                              ]
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;
        }

        if (normalizedPath.Equals($"{clientPrefix}src/app/app.config.ts", StringComparison.OrdinalIgnoreCase))
        {
            return """
                import { ApplicationConfig } from '@angular/core';
                import { provideHttpClient, withInterceptors } from '@angular/common/http';
                import { apiBaseInterceptor } from './core/interceptors/api-base.interceptor';

                export const appConfig: ApplicationConfig = {
                  providers: [
                    provideHttpClient(withInterceptors([apiBaseInterceptor]))
                  ]
                };
                """;
        }

        if (normalizedPath.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase))
        {
            return PatchOrGenerateProgramCs(existingContent, serverRoot);
        }

        if (normalizedPath.EndsWith("AuthController.cs", StringComparison.OrdinalIgnoreCase))
        {
            return PatchOrGenerateAuthController(existingContent);
        }

        if (normalizedPath.Equals($"{clientPrefix}src/app/core/services/auth.service.ts", StringComparison.OrdinalIgnoreCase))
        {
            return PatchAuthService(existingContent);
        }

        if (normalizedPath.Equals($"{clientPrefix}src/app/core/services/signalr.service.ts", StringComparison.OrdinalIgnoreCase))
        {
            return PatchSignalRService(existingContent);
        }

        if (normalizedPath.Equals("docs/DEPLOYMENT.md", StringComparison.OrdinalIgnoreCase))
        {
            return """
                # Split-origin deployment (Vercel + Railway)

                ## Vercel (frontend)
                - `IDAARA_API_URL` or `NG_APP_API_URL` → Railway API URL (no trailing slash)
                - `vercel.json` is SPA-only (no `/api` proxy rewrites)
                - Build runs `scripts/write-api-env.mjs` before `ng build`

                ## Railway (API)
                - `AllowedOrigins__0` → production Vercel URL
                - `AllowedOrigins__1` → additional preview origins as needed
                - CORS must allow `*.vercel.app` preview deployments

                ## Auth
                - Refresh cookies: `SameSite=None; Secure` in Production
                - Angular auth requests: `withCredentials: true`
                """;
        }

        if (normalizedPath.Equals($"{clientPrefix}src/environments/environment.ts", StringComparison.OrdinalIgnoreCase))
        {
            return """
                export const environment = {
                  production: false,
                  apiBaseUrl: ''
                };
                """;
        }

        if (normalizedPath.Equals($"{clientPrefix}src/environments/environment.production.ts", StringComparison.OrdinalIgnoreCase))
        {
            return """
                export const environment = {
                  production: true,
                  apiBaseUrl: ''
                };
                """;
        }

        return normalizedPath.Contains("vercel.json", StringComparison.OrdinalIgnoreCase)
            ? GenerateSpaOnlyVercelJson(website, outputDirectory)
            : null;
    }

    private static string? PatchOrGenerateProgramCs(string? existing, string serverRoot)
    {
        if (!string.IsNullOrWhiteSpace(existing))
        {
            if (existing.Contains("AllowedOrigins", StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            const string corsBlock = """

                builder.Services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                    {
                        policy.SetIsOriginAllowed(origin =>
                                origin.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase) ||
                                builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()?
                                    .Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase)) == true)
                            .AllowAnyHeader()
                            .AllowAnyMethod()
                            .AllowCredentials();
                    });
                });
                """;

            var insertAt = existing.LastIndexOf("var app = builder.Build();", StringComparison.Ordinal);
            if (insertAt >= 0)
            {
                return existing.Insert(insertAt, corsBlock);
            }
        }

        return $$"""
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.SetIsOriginAllowed(origin =>
                            origin.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase) ||
                            builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()?
                                .Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase)) == true)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
            });

            var app = builder.Build();
            app.UseForwardedHeaders();
            app.UseCors();
            app.MapControllers();
            app.Run();
            """;
    }

    private static string? PatchOrGenerateAuthController(string? existing)
    {
        if (!string.IsNullOrWhiteSpace(existing))
        {
            if (existing.Contains("SameSiteMode.None", StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            return null;
        }

        return """
            using Microsoft.AspNetCore.Mvc;

            namespace DeployAI.Generated;

            [ApiController]
            [Route("api/v1/auth")]
            public sealed class AuthController : ControllerBase
            {
                [HttpPost("refresh")]
                public IActionResult Refresh() => Ok();
            }
            """;
    }

    private static string? PatchAuthService(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return null;
        }

        if (existing.Contains("withCredentials", StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        return existing.Replace(
            "this.http.post",
            "this.http.post",
            StringComparison.Ordinal) + "\n// TODO: add withCredentials: true to login, refresh, and logout requests.\n";
    }

    private static string? PatchSignalRService(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return null;
        }

        if (existing.Contains("environment.apiBaseUrl", StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        return existing + "\n// TODO: use `${environment.apiBaseUrl}/hubs/...` for production SignalR connections.\n";
    }

    private static string GenerateSpaOnlyVercelJson(DeploymentPlanPart website, string outputDirectory)
    {
        var buildCommand = website.BuildCommand ?? "npm ci && node scripts/write-api-env.mjs && npm run build";
        return $$"""
            {
              "version": 2,
              "buildCommand": "{{buildCommand}}",
              "outputDirectory": "{{outputDirectory}}",
              "framework": null,
              "rewrites": [
                { "source": "/(.*)", "destination": "/index.html" }
              ]
            }
            """;
    }

    private static string GuessProjectName(string clientRoot)
    {
        if (string.IsNullOrWhiteSpace(clientRoot))
        {
            return "app";
        }

        var segment = clientRoot.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "app";
        return segment.Replace(".client", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string? root) =>
        root?.Trim().Trim('/') ?? string.Empty;
}
