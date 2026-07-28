namespace DeployAI.Api.Services;

/// <summary>How a website's frontend code should reach its API, which determines which env vars get wired where.</summary>
internal enum CrossProviderWiringMode
{
    /// <summary>Frontend calls relative /api paths that the website host proxies to the backend (e.g. via vercel.json rewrites) - both sides live on the same effective origin.</summary>
    SameOriginProxy,
    /// <summary>Angular + .NET: the frontend calls the backend's own origin directly, and the backend needs CORS configured for the frontend's origin(s).</summary>
    SplitOrigin,
    /// <summary>Frontend frameworks (Next/Nuxt/Vite/etc.) that bake a full API URL into the client bundle at build time rather than using relative paths or a proxy.</summary>
    DirectClientApi
}

/// <summary>
/// Resolves which environment variable keys and values need to be set on a project's website and
/// server deploy targets so the two can talk to each other (API URL on the website, CORS/frontend
/// URL on the server) - the logic behind DeployAI's automatic cross-provider env wiring.
/// </summary>
internal static class CrossProviderUrlWiring
{
    internal const string DeployAiApiProxyRouteName = "DeployAI API Proxy";
    internal const string DeployAiHubsProxyRouteName = "DeployAI Hubs Proxy";

    internal static readonly IReadOnlyList<string> IgnoredRailwayEnvKeys =
    [
        "ALLOWED_ORIGINS",
        "CORS_ALLOWED_ORIGINS",
        "CORS_ORIGIN",
        "Cors__AllowedOrigins",
        "App__ApiUrl",
        "API_URL",
        "FRONTEND_URL"
    ];

    /// <summary>Ensures a URL has an https:// (or existing http://) scheme and no trailing slash, for consistent origin comparisons.</summary>
    internal static string NormalizeOrigin(string url)
    {
        var trimmed = url.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"https://{trimmed}";
        }

        return trimmed.TrimEnd('/');
    }

    /// <summary>Whether a frontend framework calls the API via relative paths (Angular, or unrecognized frameworks by default) rather than baking in a full URL at build time.</summary>
    internal static bool UsesRelativeApiPaths(string? framework)
    {
        if (string.IsNullOrWhiteSpace(framework))
        {
            return true;
        }

        return framework.ToLowerInvariant() switch
        {
            "angular" => true,
            "next" or "nextjs" or "next.js" => false,
            "nuxt" => false,
            "vite" or "react" => false,
            "sveltekit" or "astro" => false,
            _ => true
        };
    }

    /// <summary>
    /// <paramref name="singleOriginCompose"/> is the escape hatch for plans that put both halves
    /// behind one origin (see <see cref="DeploymentPlanKind.CoolifyCompose"/>). Framework alone
    /// can't tell the two shapes apart — Angular + .NET is split-origin on Vercel/Railway and
    /// same-origin under compose — so callers that know the plan shape must say so.
    /// </summary>
    internal static CrossProviderWiringMode ResolveWiringMode(
        string? websiteFramework,
        string? serverFramework,
        bool singleOriginCompose = false)
    {
        if (singleOriginCompose)
        {
            return CrossProviderWiringMode.SameOriginProxy;
        }

        if (!UsesRelativeApiPaths(websiteFramework))
        {
            return CrossProviderWiringMode.DirectClientApi;
        }

        // SplitOrigin (Angular + .NET split-origin templates/readiness checks) requires an
        // explicit Angular signal on the frontend, not just "unrecognized framework" falling
        // through UsesRelativeApiPaths' default. Without this, an unknown frontend framework
        // paired with a generically-"docker" Railway backend (which may not be .NET at all)
        // gets misclassified as split-origin and produces irrelevant Angular/.NET Blocking
        // readiness findings for an unrelated stack.
        if (IsRecognizedSplitOriginFrontend(websiteFramework) && IsDotnetServerFramework(serverFramework))
        {
            return CrossProviderWiringMode.SplitOrigin;
        }

        return CrossProviderWiringMode.SameOriginProxy;
    }

    private static bool IsRecognizedSplitOriginFrontend(string? framework) =>
        framework?.ToLowerInvariant() is "angular";

    internal static bool ShouldUseSplitOrigin(
        string? websiteFramework,
        string? serverFramework,
        bool singleOriginCompose = false) =>
        ResolveWiringMode(websiteFramework, serverFramework, singleOriginCompose) ==
        CrossProviderWiringMode.SplitOrigin;

    /// <summary>Resolves the website-side env var key(s) that should carry the API base URL, per frontend framework convention.</summary>
    internal static IReadOnlyList<string> ResolveApiEnvKeys(string? framework)
    {
        return framework?.ToLowerInvariant() switch
        {
            "next" or "nextjs" or "next.js" => ["NEXT_PUBLIC_API_URL", "API_URL"],
            "nuxt" => ["NUXT_PUBLIC_API_URL", "API_URL"],
            "vite" or "react" => ["VITE_API_URL", "API_URL"],
            "angular" => ["DEPLOYAI_API_URL", "API_BASE_URL", "NG_APP_API_URL", "API_URL"],
            "sveltekit" or "astro" => ["PUBLIC_API_URL", "API_URL"],
            _ => ["API_URL"]
        };
    }

    /// <summary>Resolves the server-side env var key(s) that should carry allowed CORS origins/frontend URL, per backend framework convention.</summary>
    internal static IReadOnlyList<string> ResolveServerCorsEnvKeys(string? framework)
    {
        if (string.IsNullOrWhiteSpace(framework))
        {
            return DefaultServerCorsKeys;
        }

        return framework.ToLowerInvariant() switch
        {
            "dotnet" or "aspnet" or "aspnetcore" or "docker" =>
            [
                "App__BaseUrl",
                "App__FrontendUrl"
            ],
            "node" or "express" or "nestjs" =>
            [
                "FRONTEND_URL",
                "CORS_ORIGIN",
                "CORS_ALLOWED_ORIGINS",
                "ALLOWED_ORIGINS"
            ],
            _ => DefaultServerCorsKeys
        };
    }

    /// <summary>Resolves the server-side env var key(s) that should carry its own public API URL (for a same-origin proxy setup where the server needs to know its own external address).</summary>
    internal static IReadOnlyList<string> ResolveServerApiEnvKeys(string? framework)
    {
        return framework?.ToLowerInvariant() switch
        {
            "dotnet" or "aspnet" or "aspnetcore" => ["App__ApiUrl", "API_URL"],
            "node" or "express" or "nestjs" => ["API_URL", "PUBLIC_API_URL"],
            _ => ["App__ApiUrl", "API_URL"]
        };
    }

    /// <summary>Resolves the API base URL a website's own client-side code should call: the website's own origin for relative-path frameworks, otherwise the server's origin directly.</summary>
    internal static string ResolvePublicApiBaseUrl(string? websiteFramework, string publicWebsiteUrl, string apiUrl)
    {
        var website = NormalizeOrigin(publicWebsiteUrl);
        var api = NormalizeOrigin(apiUrl);
        return UsesRelativeApiPaths(websiteFramework) ? website : api;
    }

    /// <summary>Formats a set of origins into a comma-separated, deduplicated, normalized list for a single CORS env var value.</summary>
    internal static string FormatAllowedOrigins(IEnumerable<string> origins)
    {
        return string.Join(
            ",",
            origins
                .Where(origin => !string.IsNullOrWhiteSpace(origin))
                .Select(NormalizeOrigin)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Whether a key is one of ASP.NET's indexed-array config keys (AllowedOrigins__0, __1, ...) rather than a single comma-separated CORS value.</summary>
    internal static bool IsIndexedAllowedOriginsKey(string key) =>
        key.StartsWith("AllowedOrigins__", StringComparison.OrdinalIgnoreCase);

    /// <summary>Combines the primary website URL with any additional known origins (e.g. custom domains) into a normalized, deduplicated list.</summary>
    internal static IReadOnlyList<string> NormalizeWebsiteOrigins(
        string primaryWebsiteUrl,
        IEnumerable<string> websiteOrigins)
    {
        return new[] { primaryWebsiteUrl }
            .Concat(websiteOrigins)
            .Select(NormalizeOrigin)
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Whether a key holds a comma-separated list of allowed origins (as opposed to a single URL value).</summary>
    internal static bool IsCorsListEnvKey(string key) =>
        key.Equals("CORS_ALLOWED_ORIGINS", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ALLOWED_ORIGINS", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("CORS_ORIGIN", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Auth__AllowedOrigins", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("App__AllowedOrigins", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Cors__AllowedOrigins", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("App__Cors__AllowedOrigins", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a key is a legacy same-origin-proxy CORS/API key that split-origin ASP.NET apps don't read - set means it's stale config a user should remove.</summary>
    internal static bool IsIgnoredRailwayEnvKey(string key) =>
        IgnoredRailwayEnvKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the full set of server-side env var assignments (CORS origins, frontend URL, API URL)
    /// for a website+server pair, branching on <see cref="ResolveWiringMode"/> - split-origin
    /// assigns indexed AllowedOrigins__N + App__BaseUrl/FrontendUrl; same-origin-proxy assigns the
    /// framework's CORS keys plus (for ASP.NET) the legacy Auth__AllowedOrigins/indexed keys too.
    /// </summary>
    internal static IReadOnlyList<ServerRuntimeEnvAssignment> BuildServerRuntimeEnvAssignments(
        string? serverFramework,
        string? websiteFramework,
        string primaryWebsiteUrl,
        IEnumerable<string> websiteOrigins,
        string apiUrl,
        bool singleOriginCompose = false)
    {
        var mode = ResolveWiringMode(websiteFramework, serverFramework, singleOriginCompose);
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var website = NormalizeOrigin(primaryWebsiteUrl);
        var api = NormalizeOrigin(apiUrl);
        var originList = NormalizeWebsiteOrigins(primaryWebsiteUrl, websiteOrigins);

        if (mode == CrossProviderWiringMode.SplitOrigin)
        {
            for (var index = 0; index < originList.Count; index++)
            {
                assignments[$"AllowedOrigins__{index}"] = originList[index];
            }

            assignments["App__BaseUrl"] = website;
            assignments["App__FrontendUrl"] = website;
            return assignments
                .Select(entry => new ServerRuntimeEnvAssignment(entry.Key, entry.Value))
                .ToArray();
        }

        var publicApiBase = ResolvePublicApiBaseUrl(websiteFramework, website, api);
        var allowedOrigins = FormatAllowedOrigins(originList);

        foreach (var key in ResolveServerCorsEnvKeys(serverFramework))
        {
            assignments[key] = IsCorsListEnvKey(key) ? allowedOrigins : website;
        }

        if (IsDotnetServerFramework(serverFramework) && mode == CrossProviderWiringMode.SameOriginProxy)
        {
            assignments["Auth__AllowedOrigins"] = allowedOrigins;
            for (var index = 0; index < originList.Count; index++)
            {
                assignments[$"AllowedOrigins__{index}"] = originList[index];
            }
        }

        foreach (var key in ResolveServerApiEnvKeys(serverFramework))
        {
            assignments[key] = publicApiBase;
        }

        return assignments
            .Select(entry => new ServerRuntimeEnvAssignment(entry.Key, entry.Value))
            .ToArray();
    }

    /// <summary>Same as <see cref="BuildServerRuntimeEnvAssignments"/> but as a lookup dictionary, for drift comparison against a server's actual env vars.</summary>
    internal static IReadOnlyDictionary<string, string> BuildExpectedRailwayEnvValues(
        string? serverFramework,
        string? websiteFramework,
        string primaryWebsiteUrl,
        IEnumerable<string> websiteOrigins,
        string apiUrl) =>
        BuildServerRuntimeEnvAssignments(serverFramework, websiteFramework, primaryWebsiteUrl, websiteOrigins, apiUrl)
            .ToDictionary(assignment => assignment.Key, assignment => assignment.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the expected website-side API URL env var values, for drift comparison against the website's actual env vars.</summary>
    internal static IReadOnlyDictionary<string, string> BuildExpectedVercelEnvValues(
        string? websiteFramework,
        string apiUrl)
    {
        var normalizedApi = NormalizeOrigin(apiUrl);
        return ResolveApiEnvKeys(websiteFramework)
            .ToDictionary(key => key, _ => normalizedApi, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Warns when a server has stale same-origin-proxy CORS env vars set that a split-origin ASP.NET app won't actually read.</summary>
    internal static IReadOnlyList<string> ValidateIgnoredRailwayEnvKeys(IReadOnlyList<ProviderEnvVarSnapshot> actualEnvVars)
    {
        var warnings = new List<string>();
        foreach (var env in actualEnvVars)
        {
            if (IsIgnoredRailwayEnvKey(env.Key) && !string.IsNullOrWhiteSpace(env.Value))
            {
                warnings.Add($"Railway env {env.Key} is set but ignored by split-origin ASP.NET apps. Use AllowedOrigins__0 instead.");
            }
        }

        return warnings;
    }

    /// <summary>Compares an actual env var value to its expected value, normalizing origins/CORS lists so formatting differences (trailing slash, order) don't count as drift.</summary>
    internal static bool EnvValueMatches(string key, string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        if (IsCorsListEnvKey(key))
        {
            var actualOrigins = actual.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var expectedOrigins = expected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Equals(
                FormatAllowedOrigins(actualOrigins),
                FormatAllowedOrigins(expectedOrigins),
                StringComparison.OrdinalIgnoreCase);
        }

        if (IsIndexedAllowedOriginsKey(key))
        {
            return string.Equals(NormalizeOrigin(actual), NormalizeOrigin(expected), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(NormalizeOrigin(actual), NormalizeOrigin(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDotnetServerFramework(string? framework) =>
        framework?.ToLowerInvariant() is "dotnet" or "aspnet" or "aspnetcore" or "docker";

    private static readonly string[] DefaultServerCorsKeys =
    [
        "FRONTEND_URL",
        "CORS_ALLOWED_ORIGINS",
        "ALLOWED_ORIGINS",
        "App__FrontendUrl"
    ];

    internal readonly record struct ServerRuntimeEnvAssignment(string Key, string Value);
    internal readonly record struct ProviderEnvVarSnapshot(string Key, string? Value);
}
