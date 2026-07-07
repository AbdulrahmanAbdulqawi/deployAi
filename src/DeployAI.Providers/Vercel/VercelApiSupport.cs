using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Vercel;

public static partial class VercelApiSupport
{
    public static string SanitizeProjectName(string name)
    {
        var sanitized = name.Trim().ToLowerInvariant();
        sanitized = InvalidProjectNameChars().Replace(sanitized, "-");
        sanitized = RepeatedHyphens().Replace(sanitized, "-").Trim('-', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "app";
        }

        return sanitized.Length > 100 ? sanitized[..100] : sanitized;
    }

    public static string NormalizeGitHubRepo(string repoFullName)
    {
        var normalized = repoFullName.Trim();
        if (normalized.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["https://github.com/".Length..];
        }

        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.Trim('/');
    }

    /// <summary>
    /// Infer Angular static output path relative to the Vercel root directory.
    /// Angular 17+ application builder often uses dist/&lt;project&gt;/browser;
    /// older or differently named apps (e.g. idaara.client) may use dist/&lt;project&gt; only.
    /// </summary>
    public static string ResolveAngularOutputDirectory(string rootDirectory)
    {
        var folderName = rootDirectory.Trim().Trim('/').Split('/').Last();
        if (string.Equals(folderName, "client", StringComparison.OrdinalIgnoreCase))
        {
            return "dist/client/browser";
        }

        return $"dist/{folderName}";
    }

    public static void ApplySpaBuildDefaults(IDictionary<string, object?> settings, string rootDirectory)
    {
        var normalized = rootDirectory.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        settings["rootDirectory"] = normalized;
        settings.TryAdd("buildCommand", "npm run build");
        settings.TryAdd("installCommand", "npm install");
        if (!settings.ContainsKey("outputDirectory"))
        {
            settings["outputDirectory"] = ResolveAngularOutputDirectory(normalized);
        }
    }

    public static Dictionary<string, object?> BuildSettingsFromEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        var settings = new Dictionary<string, object?>();

        if (environment.TryGetValue("rootDirectory", out var rootDirectory) && !string.IsNullOrWhiteSpace(rootDirectory))
        {
            settings["rootDirectory"] = rootDirectory.Trim().Trim('/');
        }

        if (environment.TryGetValue("outputDirectory", out var outputDirectory) && !string.IsNullOrWhiteSpace(outputDirectory))
        {
            settings["outputDirectory"] = outputDirectory.Trim().Trim('/');
        }

        if (environment.TryGetValue("buildCommand", out var buildCommand) && !string.IsNullOrWhiteSpace(buildCommand))
        {
            settings["buildCommand"] = buildCommand;
        }

        if (environment.TryGetValue("installCommand", out var installCommand) && !string.IsNullOrWhiteSpace(installCommand))
        {
            settings["installCommand"] = installCommand;
        }

        if (environment.TryGetValue("framework", out var framework) && !string.IsNullOrWhiteSpace(framework))
        {
            settings["framework"] = framework;
        }

        if (settings.TryGetValue("rootDirectory", out var root) && root is string rootPath)
        {
            settings.TryAdd("buildCommand", "npm run build");
            settings.TryAdd("installCommand", "npm install");
            if (!settings.ContainsKey("outputDirectory"))
            {
                settings["outputDirectory"] = ResolveAngularOutputDirectory(rootPath);
            }

            if (!settings.ContainsKey("framework") &&
                string.Equals(rootPath, "client", StringComparison.OrdinalIgnoreCase))
            {
                settings["framework"] = "angular";
            }
        }

        return settings;
    }

    public static Dictionary<string, object?> BuildSettingsFromCreateRequest(CreateProviderProjectRequest request)
    {
        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(request.RootDirectory))
        {
            environment["rootDirectory"] = request.RootDirectory;
        }

        if (!string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            environment["outputDirectory"] = request.OutputDirectory;
        }

        if (!string.IsNullOrWhiteSpace(request.BuildCommand))
        {
            environment["buildCommand"] = request.BuildCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.InstallCommand))
        {
            environment["installCommand"] = request.InstallCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.Framework))
        {
            environment["framework"] = request.Framework;
        }

        return BuildSettingsFromEnvironment(environment);
    }

    public static string NormalizeExternalOrigin(string url)
    {
        var trimmed = url.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"https://{trimmed}";
        }

        return trimmed.TrimEnd('/');
    }

    public static string? ExtractPrimaryProductionAlias(IReadOnlyList<string>? aliases, string? projectName)
    {
        if (aliases is not null && aliases.Count > 0)
        {
            string? suffixedProduction = null;
            string? bareProduction = null;
            string? fallbackAlias = null;

            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) || IsPreviewAlias(alias))
                {
                    continue;
                }

                fallbackAlias ??= alias;
                var host = NormalizeAliasHost(alias);

                if (!string.IsNullOrWhiteSpace(projectName) &&
                    host.StartsWith($"{projectName}-", StringComparison.OrdinalIgnoreCase) &&
                    host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase))
                {
                    suffixedProduction ??= alias;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(projectName) &&
                    host.Equals($"{projectName}.vercel.app", StringComparison.OrdinalIgnoreCase))
                {
                    bareProduction ??= alias;
                }
            }

            if (!string.IsNullOrWhiteSpace(suffixedProduction))
            {
                return suffixedProduction;
            }

            if (!string.IsNullOrWhiteSpace(bareProduction))
            {
                return bareProduction;
            }

            if (!string.IsNullOrWhiteSpace(fallbackAlias))
            {
                return fallbackAlias;
            }
        }

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            return $"{projectName}.vercel.app";
        }

        return null;
    }

    internal static bool IsPreviewAlias(string alias)
    {
        var host = NormalizeAliasHost(alias);

        if (host.Contains("-git-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (host.EndsWith("-projects.vercel.app", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var subdomain = host[..^".vercel.app".Length];
        return subdomain.Count(c => c == '-') >= 3;
    }

    private static string NormalizeAliasHost(string alias) =>
        alias
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var vercelMessage = ParseErrorMessage(body);
        throw new DeployAIException("vercel_api_error", MapFriendlyMessage(vercelMessage, body));
    }

    public static string? ParseErrorMessage(string body)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<VercelErrorResponse>(body);
            return payload?.Error?.Message;
        }
        catch
        {
            return null;
        }
    }

    private static string MapFriendlyMessage(string? vercelMessage, string body)
    {
        var message = vercelMessage ?? string.Empty;
        var code = TryParseErrorCode(body);

        if (message.Contains("GitHub integration", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Install GitHub App", StringComparison.OrdinalIgnoreCase))
        {
            return "To create a Vercel app from GitHub, install the Vercel GitHub app at github.com/apps/vercel and grant access to your repository, then try again.";
        }

        if (string.Equals(code, "repo_links_exceeded_limit", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("more than 3 Projects", StringComparison.OrdinalIgnoreCase))
        {
            return "That GitHub app is already linked to three Vercel projects. Pick an existing Vercel app instead, or remove a link in the Vercel dashboard.";
        }

        if (message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "project_name_conflict", StringComparison.OrdinalIgnoreCase))
        {
            return "A Vercel app with that name already exists. Try a different name.";
        }

        if (message.Contains("invalid", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("name", StringComparison.OrdinalIgnoreCase))
        {
            return "That app name is not valid on Vercel. Use lowercase letters, numbers, and hyphens only.";
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        return "Vercel could not complete that request. Check your Vercel connection and try again.";
    }

    private static string? TryParseErrorCode(string body)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<VercelErrorResponse>(body);
            return payload?.Error?.Code;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"[^a-z0-9._-]")]
    private static partial Regex InvalidProjectNameChars();

    [GeneratedRegex(@"-+")]
    private static partial Regex RepeatedHyphens();

    private sealed class VercelErrorResponse
    {
        [JsonPropertyName("error")]
        public VercelErrorDetail? Error { get; set; }
    }

    private sealed class VercelErrorDetail
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
