namespace DeployAI.Api.Services;

internal enum CrossProviderWiringMode
{
    SameOriginProxy,
    SplitOrigin,
    DirectClientApi
}

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

    internal static CrossProviderWiringMode ResolveWiringMode(string? websiteFramework, string? serverFramework)
    {
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

    internal static bool ShouldUseSplitOrigin(string? websiteFramework, string? serverFramework) =>
        ResolveWiringMode(websiteFramework, serverFramework) == CrossProviderWiringMode.SplitOrigin;

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

    internal static IReadOnlyList<string> ResolveServerApiEnvKeys(string? framework)
    {
        return framework?.ToLowerInvariant() switch
        {
            "dotnet" or "aspnet" or "aspnetcore" => ["App__ApiUrl", "API_URL"],
            "node" or "express" or "nestjs" => ["API_URL", "PUBLIC_API_URL"],
            _ => ["App__ApiUrl", "API_URL"]
        };
    }

    internal static string ResolvePublicApiBaseUrl(string? websiteFramework, string publicWebsiteUrl, string apiUrl)
    {
        var website = NormalizeOrigin(publicWebsiteUrl);
        var api = NormalizeOrigin(apiUrl);
        return UsesRelativeApiPaths(websiteFramework) ? website : api;
    }

    internal static string FormatAllowedOrigins(IEnumerable<string> origins)
    {
        return string.Join(
            ",",
            origins
                .Where(origin => !string.IsNullOrWhiteSpace(origin))
                .Select(NormalizeOrigin)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    internal static bool IsIndexedAllowedOriginsKey(string key) =>
        key.StartsWith("AllowedOrigins__", StringComparison.OrdinalIgnoreCase);

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

    internal static bool IsCorsListEnvKey(string key) =>
        key.Equals("CORS_ALLOWED_ORIGINS", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ALLOWED_ORIGINS", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("CORS_ORIGIN", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Auth__AllowedOrigins", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("App__AllowedOrigins", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Cors__AllowedOrigins", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("App__Cors__AllowedOrigins", StringComparison.OrdinalIgnoreCase);

    internal static bool IsIgnoredRailwayEnvKey(string key) =>
        IgnoredRailwayEnvKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<ServerRuntimeEnvAssignment> BuildServerRuntimeEnvAssignments(
        string? serverFramework,
        string? websiteFramework,
        string primaryWebsiteUrl,
        IEnumerable<string> websiteOrigins,
        string apiUrl)
    {
        var mode = ResolveWiringMode(websiteFramework, serverFramework);
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

    internal static IReadOnlyDictionary<string, string> BuildExpectedRailwayEnvValues(
        string? serverFramework,
        string? websiteFramework,
        string primaryWebsiteUrl,
        IEnumerable<string> websiteOrigins,
        string apiUrl) =>
        BuildServerRuntimeEnvAssignments(serverFramework, websiteFramework, primaryWebsiteUrl, websiteOrigins, apiUrl)
            .ToDictionary(assignment => assignment.Key, assignment => assignment.Value, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, string> BuildExpectedVercelEnvValues(
        string? websiteFramework,
        string apiUrl)
    {
        var normalizedApi = NormalizeOrigin(apiUrl);
        return ResolveApiEnvKeys(websiteFramework)
            .ToDictionary(key => key, _ => normalizedApi, StringComparer.OrdinalIgnoreCase);
    }

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
