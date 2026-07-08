using DeployAI.Api.Services.DeploymentTemplates;
using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

public sealed class DeploymentFileScaffolder
{
    private readonly DeploymentTemplateResolver _templateResolver;

    public DeploymentFileScaffolder(DeploymentTemplateResolver templateResolver)
    {
        _templateResolver = templateResolver;
    }

    internal static IReadOnlyList<string> PatchablePaths { get; } =
    [
        "Program.cs",
        "Controllers/AuthController.cs",
        "app.config.ts",
        "auth.service.ts",
        "angular.json"
    ];

    internal static bool RequiresExistingContent(string path)
    {
        var fileName = path.Replace('\\', '/').TrimStart('/').Split('/').LastOrDefault() ?? path;
        return fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("AuthController.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("app.config.ts", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("auth.service.ts", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("angular.json", StringComparison.OrdinalIgnoreCase);
    }

    internal IReadOnlyList<GeneratedDeploymentFile> ScaffoldMissingFiles(
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

        var resolvedTemplates = _templateResolver
            .ResolveForGaps(parts, missingFiles, existingFilesByPath)
            .ToDictionary(template => template.TargetPath, StringComparer.OrdinalIgnoreCase);

        var generated = new List<GeneratedDeploymentFile>();
        foreach (var missing in missingFiles)
        {
            if (missing.Severity is not DeploymentFileSeverity.Blocking and not DeploymentFileSeverity.Recommended)
            {
                continue;
            }

            string? existing = null;
            existingFilesByPath?.TryGetValue(missing.Path, out existing);
            resolvedTemplates.TryGetValue(missing.Path, out var resolvedTemplate);

            var content = TryGenerateFileContent(
                missing.Path,
                website,
                server,
                existing,
                resolvedTemplate);

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
        string? existingContent,
        ResolvedDeploymentTemplate? resolvedTemplate)
    {
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        var variables = DeploymentTemplateRenderer.BuildVariables(website, server);
        var clientPrefix = variables.ClientPrefix;
        var serverRoot = variables.ServerRoot;
        var outputDirectory = variables.OutputDirectory;
        var projectName = variables.ProjectName;

        if (resolvedTemplate?.Kind == DeploymentTemplateKind.FullFile &&
            !string.IsNullOrWhiteSpace(resolvedTemplate.RenderedContent) &&
            !RequiresPatchOnly(normalizedPath, existingContent))
        {
            return resolvedTemplate.RenderedContent;
        }

        if (normalizedPath.Equals($"{clientPrefix}angular.json", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(existingContent))
            {
                return PatchAngularJson(existingContent);
            }

            return RenderGreenfieldAngularJson(projectName);
        }

        if (normalizedPath.Equals($"{clientPrefix}src/app/app.config.ts", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(existingContent))
            {
                return PatchAppConfig(existingContent);
            }

            return RenderGreenfieldAppConfig();
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

        return normalizedPath.Contains("vercel.json", StringComparison.OrdinalIgnoreCase)
            ? GenerateSpaOnlyVercelJson(website, outputDirectory)
            : resolvedTemplate?.RenderedContent;
    }

    private static bool RequiresPatchOnly(string normalizedPath, string? existingContent)
    {
        if (string.IsNullOrWhiteSpace(existingContent))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalizedPath);
        return fileName.Equals("angular.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("app.config.ts", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("AuthController.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("auth.service.ts", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("signalr.service.ts", StringComparison.OrdinalIgnoreCase);
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

        var patched = existing
            .Replace("\"/api/Auth\"", "\"/api/v1/auth\"", StringComparison.OrdinalIgnoreCase)
            .Replace("'/api/Auth'", "'/api/v1/auth'", StringComparison.OrdinalIgnoreCase)
            .Replace("\"/api/auth\"", "\"/api/v1/auth\"", StringComparison.OrdinalIgnoreCase)
            .Replace("'/api/auth'", "'/api/v1/auth'", StringComparison.OrdinalIgnoreCase);

        return patched;
    }

    internal static string? PatchAppConfig(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return null;
        }

        if (SplitOriginClientWiringAnalyzer.RegistersApiBaseInterceptor(existing))
        {
            return existing;
        }

        var patched = existing;
        if (!patched.Contains("apiBaseInterceptor", StringComparison.Ordinal))
        {
            if (!patched.Contains("@angular/common/http", StringComparison.Ordinal))
            {
                patched = "import { provideHttpClient, withInterceptors } from '@angular/common/http';\n" + patched;
            }

            if (!patched.Contains("api-base.interceptor", StringComparison.Ordinal))
            {
                patched = "import { apiBaseInterceptor } from './core/interceptors/api-base.interceptor';\n" + patched;
            }
        }

        const string withInterceptorsToken = "withInterceptors([";
        var insertAt = patched.IndexOf(withInterceptorsToken, StringComparison.Ordinal);
        if (insertAt >= 0)
        {
            var injectionPoint = insertAt + withInterceptorsToken.Length;
            if (!patched.AsSpan(insertAt, Math.Min(patched.Length - insertAt, 80))
                    .ToString()
                    .Contains("apiBaseInterceptor", StringComparison.Ordinal))
            {
                patched = patched.Insert(injectionPoint, "apiBaseInterceptor, ");
            }
        }
        else if (patched.Contains("provideHttpClient(", StringComparison.Ordinal))
        {
            patched = patched.Replace(
                "provideHttpClient(",
                "provideHttpClient(withInterceptors([apiBaseInterceptor]), ",
                StringComparison.Ordinal);
        }
        else if (patched.Contains("providers:", StringComparison.Ordinal))
        {
            patched = patched.Replace(
                "providers:",
                "providers: [provideHttpClient(withInterceptors([apiBaseInterceptor])), ",
                StringComparison.Ordinal);
        }

        return patched;
    }

    internal static string? PatchAngularJson(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return null;
        }

        if (SplitOriginClientWiringAnalyzer.HasAngularProductionFileReplacements(existing))
        {
            return existing;
        }

        const string replacementBlock = """
            "fileReplacements": [
              {
                "replace": "src/environments/environment.ts",
                "with": "src/environments/environment.production.ts"
              }
            ]
            """;

        if (existing.Contains("\"production\"", StringComparison.Ordinal))
        {
            return existing.Replace(
                "\"production\": {",
                "\"production\": {\n                              " + replacementBlock.Trim().Replace("\n", "\n                              ") + ",",
                StringComparison.Ordinal);
        }

        return existing;
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

    private static string RenderGreenfieldAngularJson(string projectName) =>
        $$"""
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

    private static string RenderGreenfieldAppConfig() =>
        """
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
