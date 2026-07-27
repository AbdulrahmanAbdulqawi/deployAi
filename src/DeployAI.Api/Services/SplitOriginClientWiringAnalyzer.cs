using System.Text.RegularExpressions;

namespace DeployAI.Api.Services;

/// <summary>Whether a built Angular bundle actually has the split-origin API interceptor wired in (as opposed to just having the source files present but not baked into the production build).</summary>
internal sealed record SplitOriginBundleWiringAnalysis(
    bool HasWithInterceptors,
    bool HasNonEmptyApiBaseUrl,
    bool HasRelativeApiPaths,
    bool IsWired)
{
    internal static SplitOriginBundleWiringAnalysis Empty { get; } = new(false, false, false, false);
}

/// <summary>
/// Pattern-matches Angular source/service files and built JS bundles to detect split-origin wiring
/// issues: missing interceptor registration, relative /api paths that bypass it, legacy env
/// property usage, and auth route mismatches - used by both readiness scanning (source files) and
/// live deployment verification (built bundles).
/// </summary>
internal static class SplitOriginClientWiringAnalyzer
{
    private static readonly Regex RelativeApiPathRegex = new(
        @"(?:apiUrl\s*=\s*['""]|apiUrl\s*:\s*['""]|`\$\{this\.apiUrl\})[/\\]?api[/\\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RelativeApiUrlAssignmentRegex = new(
        @"apiUrl\s*=\s*['""](/api[^'""]*)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyApiUrlPropertyRegex = new(
        @"environment\.apiUrl\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ApiBaseUrlPropertyRegex = new(
        @"apiBaseUrl\s*:\s*['""]([^'""]*)['""]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether app.config.ts registers the api-base interceptor.</summary>
    internal static bool RegistersApiBaseInterceptor(string? appConfig)
    {
        if (string.IsNullOrWhiteSpace(appConfig))
        {
            return false;
        }

        return appConfig.Contains("apiBaseInterceptor", StringComparison.OrdinalIgnoreCase) ||
               appConfig.Contains("api-base.interceptor", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether angular.json swaps in environment.production for production builds - needed for apiBaseUrl to actually be non-empty in the built bundle.</summary>
    internal static bool HasAngularProductionFileReplacements(string? angularJson)
    {
        if (string.IsNullOrWhiteSpace(angularJson))
        {
            return false;
        }

        return angularJson.Contains("fileReplacements", StringComparison.OrdinalIgnoreCase) &&
               angularJson.Contains("environment.production", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether any Angular service source still uses a relative /api path, which bypasses the interceptor's absolute-URL rewriting.</summary>
    internal static bool HasRelativeApiServicePaths(IEnumerable<string?> sources)
    {
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            if (RelativeApiPathRegex.IsMatch(source) || RelativeApiUrlAssignmentRegex.IsMatch(source))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Extracts the specific relative /api paths found (for pointing at exactly which service files need fixing).</summary>
    internal static IReadOnlyList<string> ExtractRelativeApiServicePaths(IEnumerable<string?> sources)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            foreach (Match match in RelativeApiUrlAssignmentRegex.Matches(source))
            {
                var path = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }
        }

        return paths.ToArray();
    }

    /// <summary>Whether the frontend calls a legacy /api/Auth route while the server only exposes /api/v1/auth (a stale-generated-code mismatch).</summary>
    internal static bool HasAuthRouteMismatch(string? authService, string? authController)
    {
        if (string.IsNullOrWhiteSpace(authService))
        {
            return false;
        }

        var usesLegacyAuthPath = authService.Contains("/api/Auth", StringComparison.OrdinalIgnoreCase);
        if (!usesLegacyAuthPath)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(authController))
        {
            return true;
        }

        return authController.Contains("api/v1/auth", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether services still reference the old environment.apiUrl property while environment files were never updated to add apiBaseUrl (a partial-migration signal).</summary>
    internal static bool UsesLegacyApiUrlPropertyWithoutApiBaseUrl(
        IReadOnlyList<string?> environmentFiles,
        IEnumerable<string?> serviceSources)
    {
        var envJoined = string.Join('\n', environmentFiles.Where(static f => !string.IsNullOrWhiteSpace(f)));
        var hasApiBaseUrl = envJoined.Contains("apiBaseUrl", StringComparison.OrdinalIgnoreCase);
        if (hasApiBaseUrl)
        {
            return false;
        }

        foreach (var source in serviceSources)
        {
            if (!string.IsNullOrWhiteSpace(source) && LegacyApiUrlPropertyRegex.IsMatch(source))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a server's configured CORS origin no longer matches the website's current live origin (e.g. after a domain change).</summary>
    internal static bool IsStaleCorsOrigin(string? configuredOrigin, string? currentWebsiteOrigin)
    {
        if (string.IsNullOrWhiteSpace(configuredOrigin) || string.IsNullOrWhiteSpace(currentWebsiteOrigin))
        {
            return false;
        }

        return !string.Equals(
            CrossProviderUrlWiring.NormalizeOrigin(configuredOrigin),
            CrossProviderUrlWiring.NormalizeOrigin(currentWebsiteOrigin),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Analyzes a deployed site's actual built JS bundles for split-origin wiring - the ground truth check, since source files can be correct but not actually compiled in.</summary>
    internal static SplitOriginBundleWiringAnalysis AnalyzeBundleScripts(IEnumerable<string> scriptBodies)
    {
        var joined = string.Join('\n', scriptBodies.Where(static body => !string.IsNullOrWhiteSpace(body)));
        if (string.IsNullOrWhiteSpace(joined))
        {
            return SplitOriginBundleWiringAnalysis.Empty;
        }

        var hasWithInterceptors = joined.Contains("withInterceptors", StringComparison.Ordinal);
        var hasRelativeApiPaths = RelativeApiUrlAssignmentRegex.IsMatch(joined) ||
                                  joined.Contains("apiUrl=\"/api", StringComparison.OrdinalIgnoreCase) ||
                                  joined.Contains("apiUrl='/api", StringComparison.OrdinalIgnoreCase);

        var hasNonEmptyApiBaseUrl = false;
        foreach (Match match in ApiBaseUrlPropertyRegex.Matches(joined))
        {
            if (!string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                hasNonEmptyApiBaseUrl = true;
                break;
            }
        }

        var isWired = hasWithInterceptors &&
                      hasNonEmptyApiBaseUrl &&
                      (!hasRelativeApiPaths || hasWithInterceptors);

        return new SplitOriginBundleWiringAnalysis(
            hasWithInterceptors,
            hasNonEmptyApiBaseUrl,
            hasRelativeApiPaths,
            isWired);
    }

    /// <summary>Filters a repo's fetched files down to just Angular *.service.ts files.</summary>
    internal static IEnumerable<string?> SelectServiceFileContents(
        IReadOnlyDictionary<string, string?> fileContentsByPath)
    {
        return fileContentsByPath
            .Where(entry => entry.Key.Contains(".service.ts", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value);
    }
}
