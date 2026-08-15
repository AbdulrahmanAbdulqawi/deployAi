using DeployAI.Api.Services.DeploymentTemplates;
using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

/// <summary>
/// Generates split-origin setup files directly from static templates (no AI call) where possible,
/// and patches existing repo files (e.g. Program.cs, AuthController.cs) in place for the subset of
/// files where a full-file template would clobber repo-specific code.
/// </summary>
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

        // One file can fail several rules at once — a compose file that both publishes host ports
        // and omits a service is two findings about one path — and each resolves to the same
        // template. ToDictionary threw on the second, which took down the whole setup run: the
        // request died mid-stream, so the UI kept counting up with no error and no PR. It never
        // fired before only because those findings named a path no template matched, so both were
        // skipped; the silent drop was hiding the crash rather than avoiding it.
        var resolvedTemplates = _templateResolver
            .ResolveForGaps(parts, missingFiles, existingFilesByPath)
            .GroupBy(template => template.TargetPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

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

        // Same reason as the grouping above: several findings about one file must still produce one
        // file, or it is committed twice in a single tree.
        return generated
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
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
        var serverNamespace = variables.ServerNamespace;

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
            return PatchOrGenerateAuthController(existingContent, serverNamespace);
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

    private static string? PatchOrGenerateAuthController(string? existing, string serverNamespace)
    {
        if (!string.IsNullOrWhiteSpace(existing))
        {
            if (existing.Contains("SameSiteMode.None", StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            return PatchCookieOptionsForCrossOrigin(existing);
        }

        return $$"""
            using Microsoft.AspNetCore.Mvc;

            namespace {{serverNamespace}};

            [ApiController]
            [Route("api/v1/auth")]
            public sealed class AuthController : ControllerBase
            {
                [HttpPost("refresh")]
                public IActionResult Refresh() => Ok();
            }
            """;
    }

    /// <summary>
    /// Injects `SameSite = SameSiteMode.None, Secure = true,` into any existing
    /// `new CookieOptions { ... }` initializer that doesn't already set SameSite, so
    /// refresh/login cookies survive cross-origin split-origin requests. Falls back to a
    /// TODO marker (rather than silently claiming the gap is resolved) if no CookieOptions
    /// initializer is found to patch deterministically.
    /// </summary>
    private static string PatchCookieOptionsForCrossOrigin(string existing)
    {
        var sb = new System.Text.StringBuilder();
        var pos = 0;
        var patchedAny = false;
        while (true)
        {
            var start = existing.IndexOf("new CookieOptions", pos, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(existing, pos, existing.Length - pos);
                break;
            }

            var braceOpen = existing.IndexOf('{', start);
            if (braceOpen < 0)
            {
                sb.Append(existing, pos, existing.Length - pos);
                break;
            }

            var braceClose = FindMatchingBraceForward(existing, braceOpen);
            if (braceClose < 0)
            {
                sb.Append(existing, pos, braceOpen + 1 - pos);
                pos = braceOpen + 1;
                continue;
            }

            sb.Append(existing, pos, braceOpen + 1 - pos);
            var body = existing[(braceOpen + 1)..braceClose];
            if (!body.Contains("SameSite", StringComparison.Ordinal))
            {
                sb.Append(" SameSite = SameSiteMode.None, Secure = true,");
                patchedAny = true;
            }

            sb.Append(body);
            sb.Append('}');
            pos = braceClose + 1;
        }

        var patched = sb.ToString();
        return patchedAny
            ? patched
            : existing + "\n// TODO: set SameSite=None; Secure=true (Production only) on refresh/login cookie options.\n";
    }

    private static int FindMatchingBraceForward(string content, int openIndex)
    {
        var depth = 0;
        var inString = '\0';
        for (var i = openIndex; i < content.Length; i++)
        {
            var c = content[i];
            if (inString != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == inString)
                {
                    inString = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                inString = c;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string? PatchAuthService(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return null;
        }

        // Match `/api/Auth` (or `/api/auth`) as a whole path segment, with an optional
        // sub-path (e.g. `/api/Auth/login`). An exact-string Replace would miss the
        // sub-path case shown in the patch instructions' own example
        // (`this.http.post('/api/Auth/login', body)`), silently leaving it unfixed.
        var patched = System.Text.RegularExpressions.Regex.Replace(
            existing,
            "(['\"])/api/[Aa]uth(/[^'\"]*)?\\1",
            match => $"{match.Groups[1].Value}/api/v1/auth{match.Groups[2].Value}{match.Groups[1].Value}");

        patched = InsertWithCredentials(patched);

        return patched;
    }

    /// <summary>
    /// Inserts <c>withCredentials: true</c> into every <c>this.http.*(...)</c> call so
    /// auth cookies are sent cross-origin. Uses balanced paren/brace scanning (not a
    /// simple regex) so it handles generics (`this.http.post&lt;T&gt;(...)`), multi-line
    /// calls, and an existing trailing options object literal without corrupting syntax.
    /// </summary>
    private static string InsertWithCredentials(string content)
    {
        var sb = new System.Text.StringBuilder();
        var pos = 0;
        while (true)
        {
            var callStart = content.IndexOf("this.http.", pos, StringComparison.Ordinal);
            if (callStart < 0)
            {
                sb.Append(content, pos, content.Length - pos);
                break;
            }

            var parenOpen = content.IndexOf('(', callStart);
            if (parenOpen < 0)
            {
                sb.Append(content, pos, content.Length - pos);
                break;
            }

            var parenClose = FindMatchingParen(content, parenOpen);
            if (parenClose < 0)
            {
                sb.Append(content, pos, parenOpen + 1 - pos);
                pos = parenOpen + 1;
                continue;
            }

            sb.Append(content, pos, parenOpen + 1 - pos);
            var args = content[(parenOpen + 1)..parenClose];
            sb.Append(InjectWithCredentialsIntoArgs(args));
            sb.Append(')');
            pos = parenClose + 1;
        }

        return sb.ToString();
    }

    private static int FindMatchingParen(string content, int openIndex)
    {
        var depth = 0;
        var inString = '\0';
        for (var i = openIndex; i < content.Length; i++)
        {
            var c = content[i];
            if (inString != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == inString)
                {
                    inString = '\0';
                }

                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                inString = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string InjectWithCredentialsIntoArgs(string args)
    {
        if (args.Contains("withCredentials", StringComparison.Ordinal))
        {
            return args;
        }

        var trimmed = args.TrimEnd();
        var trailingWhitespace = args[trimmed.Length..];

        if (trimmed.EndsWith('}'))
        {
            var openBrace = FindMatchingBraceBackward(trimmed, trimmed.Length - 1);
            if (openBrace >= 0)
            {
                return trimmed[..(openBrace + 1)] + " withCredentials: true, " + trimmed[(openBrace + 1)..] + trailingWhitespace;
            }
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "{ withCredentials: true }";
        }

        return trimmed + ", { withCredentials: true }" + trailingWhitespace;
    }

    private static int FindMatchingBraceBackward(string content, int closeIndex)
    {
        var depth = 0;
        for (var i = closeIndex; i >= 0; i--)
        {
            var c = content[i];
            if (c == '}')
            {
                depth++;
            }
            else if (c == '{')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
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

    /// <summary>
    /// Rewrites the first relative `/hubs/...` string literal into a conditional expression
    /// that builds an absolute URL from `environment.apiBaseUrl` in production. Falls back to
    /// a TODO marker (not a silent no-op) when no such literal can be located deterministically,
    /// so hybrid mode's AI fallback still has an accurate signal that the gap wasn't resolved.
    /// </summary>
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

        var hubUrlPattern = new System.Text.RegularExpressions.Regex(@"(['""`])(/hubs/[A-Za-z0-9_\-/]*)\1");
        var match = hubUrlPattern.Match(existing);
        if (!match.Success)
        {
            return existing + "\n// TODO: use `${environment.apiBaseUrl}/hubs/...` for production SignalR connections.\n";
        }

        var hubPath = match.Groups[2].Value;
        var replacement = $$"""(environment.production && environment.apiBaseUrl ? `${environment.apiBaseUrl.replace(/\/$/, '')}{{hubPath}}` : '{{hubPath}}')""";

        var patched = existing[..match.Index] + replacement + existing[(match.Index + match.Length)..];

        if (!patched.Contains("environments/environment'", StringComparison.Ordinal) &&
            !patched.Contains("environments/environment\"", StringComparison.Ordinal))
        {
            patched = "import { environment } from '../../../environments/environment';\n" + patched;
        }

        return patched;
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
