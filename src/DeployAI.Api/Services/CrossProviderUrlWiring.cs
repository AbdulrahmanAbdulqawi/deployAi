namespace DeployAI.Api.Services;

internal static class CrossProviderUrlWiring
{
    internal const string DeployAiApiProxyRouteName = "DeployAI API Proxy";
    internal const string DeployAiHubsProxyRouteName = "DeployAI Hubs Proxy";

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

    internal static IReadOnlyList<string> ResolveApiEnvKeys(string? framework)
    {
        return framework?.ToLowerInvariant() switch
        {
            "next" or "nextjs" or "next.js" => ["NEXT_PUBLIC_API_URL", "API_URL"],
            "nuxt" => ["NUXT_PUBLIC_API_URL", "API_URL"],
            "vite" or "react" => ["VITE_API_URL", "API_URL"],
            "angular" => ["NG_APP_API_URL", "API_URL"],
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
            "dotnet" or "aspnet" or "aspnetcore" => ["App__FrontendUrl", "CORS_ALLOWED_ORIGINS", "FRONTEND_URL"],
            "node" or "express" or "nestjs" => ["FRONTEND_URL", "CORS_ORIGIN", "CORS_ALLOWED_ORIGINS", "ALLOWED_ORIGINS"],
            _ => DefaultServerCorsKeys
        };
    }

    private static readonly string[] DefaultServerCorsKeys =
    [
        "FRONTEND_URL",
        "CORS_ALLOWED_ORIGINS",
        "ALLOWED_ORIGINS",
        "App__FrontendUrl"
    ];
}
