using System.Text.Json;
using System.Text.Json.Nodes;
using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

internal static class VercelJsonRewrites
{
    internal const string ApiSource = "/api/:path*";
    internal const string HubsSource = "/hubs/:path*";

    internal static IEnumerable<string> CandidatePaths(DeployTargetConfig config)
    {
        var root = config.RootDirectory?.Trim().Trim('/');
        if (!string.IsNullOrEmpty(root))
        {
            yield return $"{root}/vercel.json";
            yield break;
        }

        yield return "vercel.json";
        yield return "client/vercel.json";
    }

    internal static string ResolvePrimaryPath(DeployTargetConfig config)
    {
        var root = config.RootDirectory?.Trim().Trim('/');
        return string.IsNullOrEmpty(root) ? "vercel.json" : $"{root}/vercel.json";
    }

    internal static string EnrichWithBuildSettings(string vercelJsonContent, DeployTargetConfig config)
    {
        var root = JsonNode.Parse(vercelJsonContent)?.AsObject() ?? new JsonObject();

        if (root["buildCommand"] is null && !string.IsNullOrWhiteSpace(config.BuildCommand))
        {
            root["buildCommand"] = config.BuildCommand;
        }

        if (root["installCommand"] is null && !string.IsNullOrWhiteSpace(config.InstallCommand))
        {
            root["installCommand"] = config.InstallCommand;
        }

        if (root["outputDirectory"] is null && !string.IsNullOrWhiteSpace(config.OutputDirectory))
        {
            root["outputDirectory"] = config.OutputDirectory.Trim().Trim('/');
        }

        return root.ToJsonString(SerializerOptions);
    }

    internal static bool HasApiProxyAntiPattern(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var root = JsonNode.Parse(content)?.AsObject();
        if (root is null)
        {
            return false;
        }

        if (ContainsProxyRewrite(root["rewrites"] as JsonArray))
        {
            return true;
        }

        return ContainsProxyRewrite(root["routes"] as JsonArray, useLegacyKeys: true);
    }

    internal static bool TryBuildSpaOnlyContent(
        string? existingContent,
        DeployTargetConfig config,
        out string updatedContent)
    {
        var root = string.IsNullOrWhiteSpace(existingContent)
            ? new JsonObject()
            : JsonNode.Parse(existingContent)?.AsObject() ?? new JsonObject();

        var preserved = new List<JsonObject>();
        JsonObject? spaFallback = null;
        var rewrites = root["rewrites"] as JsonArray ?? new JsonArray();

        foreach (var entry in rewrites)
        {
            if (entry is not JsonObject rewrite)
            {
                continue;
            }

            var source = rewrite["source"]?.GetValue<string>();
            if (IsProxySource(source))
            {
                continue;
            }

            if (IsSpaFallback(source, rewrite["destination"]?.GetValue<string>()))
            {
                spaFallback = rewrite.DeepClone().AsObject();
                continue;
            }

            preserved.Add(rewrite.DeepClone().AsObject());
        }

        var merged = new JsonArray();
        foreach (var rewrite in preserved)
        {
            merged.Add(rewrite);
        }

        if (spaFallback is not null)
        {
            merged.Add(spaFallback);
        }
        else
        {
            merged.Add(CreateRewrite("/(.*)", "/index.html"));
        }

        root["rewrites"] = merged;
        root.Remove("routes");

        if (root["version"] is null)
        {
            root["version"] = 2;
        }

        if (root["framework"] is null)
        {
            root["framework"] = null;
        }

        var currentBuildCommand = root["buildCommand"]?.GetValue<string>() ?? config.BuildCommand;
        var ensuredBuildCommand = EnsureWriteApiEnvInBuildCommand(currentBuildCommand);
        if (!string.Equals(currentBuildCommand, ensuredBuildCommand, StringComparison.Ordinal))
        {
            root["buildCommand"] = ensuredBuildCommand;
        }

        updatedContent = EnrichWithBuildSettings(root.ToJsonString(SerializerOptions), config);
        return !string.Equals(updatedContent.Trim(), existingContent?.Trim(), StringComparison.Ordinal);
    }

    internal static string EnsureWriteApiEnvInBuildCommand(string? buildCommand)
    {
        if (buildCommand?.Contains("write-api-env", StringComparison.OrdinalIgnoreCase) == true)
        {
            return buildCommand;
        }

        if (string.IsNullOrWhiteSpace(buildCommand))
        {
            return "npm ci && node scripts/write-api-env.mjs && npm run build";
        }

        if (buildCommand.StartsWith("npm ci &&", StringComparison.OrdinalIgnoreCase))
        {
            return buildCommand.Insert("npm ci &&".Length, " node scripts/write-api-env.mjs &&");
        }

        return $"node scripts/write-api-env.mjs && {buildCommand}";
    }

    internal static bool NeedsWriteApiEnvBuildCommand(string? vercelJsonContent, DeployTargetConfig config)
    {
        if (string.IsNullOrWhiteSpace(vercelJsonContent))
        {
            return true;
        }

        var root = JsonNode.Parse(vercelJsonContent)?.AsObject();
        var buildCommand = root?["buildCommand"]?.GetValue<string>() ?? config.BuildCommand;
        return buildCommand?.Contains("write-api-env", StringComparison.OrdinalIgnoreCase) != true;
    }

    internal static bool TryStripProxyRewrites(
        string? existingContent,
        DeployTargetConfig config,
        out string updatedContent) =>
        TryBuildSpaOnlyContent(existingContent, config, out updatedContent);

    // Legacy same-origin proxy rewrites for non-Angular stacks (SameOriginProxy mode only).
    // Split-origin Angular apps must use TryBuildSpaOnlyContent instead.
    internal static bool TryBuildUpdatedContent(
        string? existingContent,
        string apiBaseUrl,
        out string updatedContent)
    {
        var normalizedApi = apiBaseUrl.TrimEnd('/');
        var apiDestination = $"{normalizedApi}/api/:path*";
        var hubsDestination = $"{normalizedApi}/hubs/:path*";

        var root = string.IsNullOrWhiteSpace(existingContent)
            ? new JsonObject()
            : JsonNode.Parse(existingContent)?.AsObject() ?? new JsonObject();

        var rewrites = root["rewrites"] as JsonArray ?? new JsonArray();
        var preserved = new List<JsonObject>();
        JsonObject? spaFallback = null;

        foreach (var entry in rewrites)
        {
            if (entry is not JsonObject rewrite)
            {
                continue;
            }

            var source = rewrite["source"]?.GetValue<string>();
            if (string.Equals(source, ApiSource, StringComparison.Ordinal) ||
                string.Equals(source, HubsSource, StringComparison.Ordinal))
            {
                continue;
            }

            if (IsSpaFallback(source, rewrite["destination"]?.GetValue<string>()))
            {
                spaFallback = rewrite.DeepClone().AsObject();
                continue;
            }

            preserved.Add(rewrite.DeepClone().AsObject());
        }

        if (HasCorrectProxyConfiguration(root, rewrites, apiDestination, hubsDestination) &&
            !string.IsNullOrWhiteSpace(existingContent))
        {
            updatedContent = existingContent;
            return false;
        }

        var merged = new JsonArray
        {
            CreateRewrite(ApiSource, apiDestination),
            CreateRewrite(HubsSource, hubsDestination)
        };

        foreach (var rewrite in preserved)
        {
            merged.Add(rewrite);
        }

        if (spaFallback is not null)
        {
            merged.Add(spaFallback);
        }
        else if (string.IsNullOrWhiteSpace(existingContent))
        {
            merged.Add(CreateRewrite("/(.*)", "/index.html"));
        }

        root["rewrites"] = merged;
        root.Remove("routes");
        updatedContent = root.ToJsonString(SerializerOptions);
        return !string.Equals(updatedContent, existingContent?.Trim(), StringComparison.Ordinal);
    }

    private static bool HasExpectedProxyRewrites(
        JsonArray rewrites,
        string apiDestination,
        string hubsDestination)
    {
        var hasApi = false;
        var hasHubs = false;

        foreach (var entry in rewrites)
        {
            if (entry is not JsonObject rewrite)
            {
                continue;
            }

            var source = rewrite["source"]?.GetValue<string>();
            var destination = rewrite["destination"]?.GetValue<string>();
            if (string.Equals(source, ApiSource, StringComparison.Ordinal) &&
                string.Equals(destination, apiDestination, StringComparison.OrdinalIgnoreCase))
            {
                hasApi = true;
            }

            if (string.Equals(source, HubsSource, StringComparison.Ordinal) &&
                string.Equals(destination, hubsDestination, StringComparison.OrdinalIgnoreCase))
            {
                hasHubs = true;
            }
        }

        return hasApi && hasHubs;
    }

    private static bool HasCorrectProxyConfiguration(
        JsonObject root,
        JsonArray rewrites,
        string apiDestination,
        string hubsDestination)
    {
        if (HasConflictingLegacyRoutes(root))
        {
            return false;
        }

        if (!HasExpectedProxyRewrites(rewrites, apiDestination, hubsDestination))
        {
            return false;
        }

        var spaIndex = -1;
        var apiIndex = -1;
        var hubsIndex = -1;

        for (var index = 0; index < rewrites.Count; index++)
        {
            if (rewrites[index] is not JsonObject rewrite)
            {
                continue;
            }

            var source = rewrite["source"]?.GetValue<string>();
            if (IsSpaFallback(source, rewrite["destination"]?.GetValue<string>()))
            {
                spaIndex = index;
            }

            if (string.Equals(source, ApiSource, StringComparison.Ordinal))
            {
                apiIndex = index;
            }

            if (string.Equals(source, HubsSource, StringComparison.Ordinal))
            {
                hubsIndex = index;
            }
        }

        if (apiIndex < 0 || hubsIndex < 0)
        {
            return false;
        }

        if (spaIndex < 0)
        {
            return true;
        }

        return apiIndex < spaIndex && hubsIndex < spaIndex;
    }

    private static bool HasConflictingLegacyRoutes(JsonObject root)
    {
        if (root["routes"] is not JsonArray routes)
        {
            return false;
        }

        foreach (var entry in routes)
        {
            if (entry is not JsonObject route)
            {
                continue;
            }

            var source = route["src"]?.GetValue<string>() ?? route["source"]?.GetValue<string>();
            var destination = route["dest"]?.GetValue<string>() ?? route["destination"]?.GetValue<string>();
            if (IsSpaFallback(source, destination))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsProxyRewrite(JsonArray? entries, bool useLegacyKeys = false)
    {
        if (entries is null)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry is not JsonObject rewrite)
            {
                continue;
            }

            var source = useLegacyKeys
                ? rewrite["src"]?.GetValue<string>() ?? rewrite["source"]?.GetValue<string>()
                : rewrite["source"]?.GetValue<string>();
            if (IsProxySource(source))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProxySource(string? source) =>
        string.Equals(source, ApiSource, StringComparison.Ordinal) ||
        string.Equals(source, HubsSource, StringComparison.Ordinal) ||
        (!string.IsNullOrWhiteSpace(source) &&
         (source.Contains("/api/", StringComparison.Ordinal) || source.Contains("/hubs/", StringComparison.Ordinal)));

    private static bool IsSpaFallback(string? source, string? destination) =>
        (string.Equals(source, "/(.*)", StringComparison.Ordinal) ||
         string.Equals(source, "/:path*", StringComparison.Ordinal)) &&
        !string.IsNullOrWhiteSpace(destination) &&
        destination.Contains("index.html", StringComparison.OrdinalIgnoreCase);

    private static JsonObject CreateRewrite(string source, string destination) =>
        new()
        {
            ["source"] = source,
            ["destination"] = destination
        };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };
}
