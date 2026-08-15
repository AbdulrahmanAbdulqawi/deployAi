using System.Text.Json;
using System.Text.RegularExpressions;
using DeployAI.Core.Deployments;

namespace DeployAI.Infrastructure.GitHub;

public interface IDatabaseRequirementDetector
{
    DatabaseRequirementProfile Detect(
        string? dockerComposeContent,
        string? appsettingsContent,
        string? prismaSchemaContent = null);
}

public sealed partial class DatabaseRequirementDetector : IDatabaseRequirementDetector
{
    public DatabaseRequirementProfile Detect(
        string? dockerComposeContent,
        string? appsettingsContent,
        string? prismaSchemaContent = null)
    {
        var requiresPostgresFromCompose = DetectPostgresInCompose(dockerComposeContent);
        var requiresRedisFromCompose = DetectRedisInCompose(dockerComposeContent);
        var (requiresPostgresFromComposeEnv, requiresRedisFromComposeEnv, composeKeys) =
            DetectConnectionStringsFromComposeEnvironment(dockerComposeContent);
        var (requiresPostgresFromSettings, requiresRedisFromSettings, settingsKeys) = DetectFromAppsettings(appsettingsContent);
        var requiresPostgresFromPrisma = DetectPostgresInPrisma(prismaSchemaContent);

        var requiresPostgres = requiresPostgresFromCompose || requiresPostgresFromComposeEnv ||
            requiresPostgresFromSettings || requiresPostgresFromPrisma;
        var requiresRedis = requiresRedisFromCompose || requiresRedisFromComposeEnv || requiresRedisFromSettings;
        var keys = MergeKeys(settingsKeys, composeKeys);
        var postgresDatabaseName = requiresPostgres
            ? ExtractPostgresDatabaseName(appsettingsContent)
                ?? ExtractPostgresDatabaseNameFromCompose(dockerComposeContent)
            : null;

        return new DatabaseRequirementProfile(requiresPostgres, requiresRedis, keys, postgresDatabaseName);
    }

    internal static bool DetectPostgresInCompose(string? dockerComposeContent)
    {
        if (string.IsNullOrWhiteSpace(dockerComposeContent))
        {
            return false;
        }

        return PostgresImageRegex().IsMatch(dockerComposeContent);
    }

    internal static bool DetectRedisInCompose(string? dockerComposeContent)
    {
        if (string.IsNullOrWhiteSpace(dockerComposeContent))
        {
            return false;
        }

        return RedisImageRegex().IsMatch(dockerComposeContent);
    }

    /// <summary>
    /// A connection string an app declares only where its docker-compose environment sets it —
    /// never in appsettings.json — because the app reads it straight from code, e.g.
    /// <c>Configuration.GetConnectionString("Postgres")</c> with no matching config-file entry.
    /// </summary>
    /// <remarks>
    /// <c>ConnectionStrings__Postgres</c> is .NET's standard double-underscore environment-variable
    /// form of the config path <c>ConnectionStrings:Postgres</c>. The compose file is already read
    /// for the postgres/redis image check; this reads the same content for the key name the app
    /// actually asks for, rather than only ever wiring the generic defaults
    /// (<c>ConnectionStrings__Default</c>, <c>DATABASE_URL</c>) that happen to match nothing here.
    /// </remarks>
    internal static (bool RequiresPostgres, bool RequiresRedis, IReadOnlyList<string> Keys)
        DetectConnectionStringsFromComposeEnvironment(string? dockerComposeContent)
    {
        if (string.IsNullOrWhiteSpace(dockerComposeContent))
        {
            return (false, false, []);
        }

        var keys = new List<string>();
        var requiresPostgres = false;
        var requiresRedis = false;

        foreach (Match match in ComposeConnectionStringRegex().Matches(dockerComposeContent))
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim();

            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(key);
            }

            if (string.Equals(key, "Redis", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("redis", StringComparison.OrdinalIgnoreCase))
            {
                requiresRedis = true;
            }

            if (LooksLikePostgresConnection(key, value))
            {
                requiresPostgres = true;
            }
        }

        return (requiresPostgres, requiresRedis, keys);
    }

    private static IReadOnlyList<string> MergeKeys(IReadOnlyList<string> primary, IReadOnlyList<string> additional)
    {
        if (additional.Count == 0)
        {
            return primary;
        }

        var merged = new List<string>(primary);
        foreach (var key in additional)
        {
            if (!merged.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(key);
            }
        }

        return merged;
    }

    internal static (bool RequiresPostgres, bool RequiresRedis, IReadOnlyList<string> Keys) DetectFromAppsettings(
        string? appsettingsContent)
    {
        if (string.IsNullOrWhiteSpace(appsettingsContent))
        {
            return (false, false, []);
        }

        try
        {
            using var document = JsonDocument.Parse(StripBom(appsettingsContent));
            if (!document.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings) ||
                connectionStrings.ValueKind != JsonValueKind.Object)
            {
                return (false, false, []);
            }

            var keys = new List<string>();
            var requiresPostgres = false;
            var requiresRedis = false;

            foreach (var property in connectionStrings.EnumerateObject())
            {
                keys.Add(property.Name);
                if (string.Equals(property.Name, "Redis", StringComparison.OrdinalIgnoreCase))
                {
                    requiresRedis = true;
                }

                var value = ReadConnectionStringValue(property.Value);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (value.Contains("redis", StringComparison.OrdinalIgnoreCase))
                {
                    requiresRedis = true;
                }

                if (LooksLikePostgresConnection(property.Name, value))
                {
                    requiresPostgres = true;
                }
            }

            return (requiresPostgres, requiresRedis, keys);
        }
        catch (JsonException)
        {
            return (false, false, []);
        }
    }

    internal static string? ExtractPostgresDatabaseNameFromCompose(string? dockerComposeContent)
    {
        if (string.IsNullOrWhiteSpace(dockerComposeContent))
        {
            return null;
        }

        var match = ComposePostgresDbRegex().Match(dockerComposeContent);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    internal static string? ExtractPostgresDatabaseName(string? appsettingsContent)
    {
        if (string.IsNullOrWhiteSpace(appsettingsContent))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(StripBom(appsettingsContent));
            if (!document.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings) ||
                connectionStrings.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? preferredName = null;
            string? fallbackName = null;

            foreach (var property in connectionStrings.EnumerateObject())
            {
                var value = ReadConnectionStringValue(property.Value);
                if (string.IsNullOrWhiteSpace(value) ||
                    string.Equals(property.Name, "Redis", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!LooksLikePostgresConnection(property.Name, value))
                {
                    continue;
                }

                var databaseName = ParseDatabaseNameFromConnectionString(value);
                if (string.IsNullOrWhiteSpace(databaseName))
                {
                    continue;
                }

                if (string.Equals(property.Name, "DefaultConnection", StringComparison.OrdinalIgnoreCase))
                {
                    preferredName = databaseName;
                    break;
                }

                fallbackName ??= databaseName;
            }

            return preferredName ?? fallbackName;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ParseDatabaseNameFromConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        if (connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
            {
                var pathName = uri.AbsolutePath.Trim('/');
                return string.IsNullOrWhiteSpace(pathName) ? null : pathName;
            }

            return null;
        }

        var match = DatabaseNameRegex().Match(connectionString);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // appsettings.json written by Visual Studio / PowerShell often carries a UTF-8 BOM. Read as a
    // string it becomes a leading U+FEFF, and JsonDocument.Parse(string) rejects it — which silently
    // turned "needs Postgres + Redis" into "needs nothing". Strip it before parsing.
    private static string StripBom(string content) =>
        content.Length > 0 && content[0] == '﻿' ? content[1..] : content;

    private static string? ReadConnectionStringValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Object => TryReadNestedConnectionString(element),
            _ => null
        };

    private static string? TryReadNestedConnectionString(JsonElement element)
    {
        foreach (var propertyName in new[] { "ConnectionString", "connectionString", "Value", "value" })
        {
            if (element.TryGetProperty(propertyName, out var nested) &&
                nested.ValueKind == JsonValueKind.String)
            {
                return nested.GetString();
            }
        }

        return null;
    }

    private static bool DetectPostgresInPrisma(string? prismaSchemaContent)
    {
        if (string.IsNullOrWhiteSpace(prismaSchemaContent))
        {
            return false;
        }

        return prismaSchemaContent.Contains("provider = \"postgresql\"", StringComparison.OrdinalIgnoreCase) ||
               prismaSchemaContent.Contains("provider = \"postgres\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePostgresConnection(string key, string value)
    {
        if (string.Equals(key, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Contains("Host=", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "DefaultConnection", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "AdminConnection", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "TenantTemplate", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"Database\s*=\s*([^;""'\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNameRegex();

    [GeneratedRegex(@"image\s*:\s*['""]?[^\s'""]*postgres", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostgresImageRegex();

    [GeneratedRegex(@"image\s*:\s*['""]?[^\s'""]*redis", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RedisImageRegex();

    [GeneratedRegex(@"POSTGRES_DB\s*:\s*['""]?([^'""\s#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComposePostgresDbRegex();

    [GeneratedRegex(
        @"ConnectionStrings__(?<key>[A-Za-z0-9_]+)\s*[:=]\s*[""']?(?<value>[^""'\r\n#]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComposeConnectionStringRegex();
}
