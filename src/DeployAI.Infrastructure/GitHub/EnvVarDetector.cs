using System.Text.Json;
using System.Text.RegularExpressions;
using DeployAI.Core.Deployments.Graph;

namespace DeployAI.Infrastructure.GitHub;

/// <summary>
/// The files an env scan can draw on. All optional — callers pass whatever the repo has.
/// </summary>
public sealed record EnvScanInputs(
    string? ComposeContent = null,
    IReadOnlyList<string>? DockerfileContents = null,
    string? DotEnvExampleContent = null,
    string? AppsettingsContent = null,
    string? ReadmeContent = null,
    IReadOnlyList<string>? GithubWorkflowContents = null);

public interface IEnvVarDetector
{
    IReadOnlyList<DetectedEnvVar> Detect(EnvScanInputs inputs);
}

/// <summary>
/// Language-agnostic env-var detection: the same variable is recognized whether it appears as
/// a compose ${VAR}, a Dockerfile ARG/ENV, a .env.example line, an empty appsettings value, a
/// README snippet, or a CI secret. Mentions are merged per name, classified (secret? which
/// smart-fill category?), and platform-injected names are dropped — asking the user to type
/// COOLIFY_URL or NODE_ENV is noise at best and a footgun at worst.
/// </summary>
public sealed partial class EnvVarDetector : IEnvVarDetector
{
    /// <summary>Injected by the platform or runtime; never something to ask a user for.</summary>
    private static readonly string[] ExcludedPrefixes =
        ["COOLIFY_", "SERVICE_FQDN_", "SERVICE_URL_", "GITHUB_", "VERCEL_", "RAILWAY_"];

    private static readonly string[] ExcludedNames =
        ["NODE_ENV", "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "PORT", "TZ", "PATH", "HOME"];

    public IReadOnlyList<DetectedEnvVar> Detect(EnvScanInputs inputs)
    {
        var mentions = new List<(string Name, string? Default, string Source)>();

        mentions.AddRange(ScanCompose(inputs.ComposeContent));
        foreach (var dockerfile in inputs.DockerfileContents ?? [])
        {
            mentions.AddRange(ScanDockerfile(dockerfile));
        }

        mentions.AddRange(ScanDotEnv(inputs.DotEnvExampleContent));
        mentions.AddRange(ScanAppsettings(inputs.AppsettingsContent));
        mentions.AddRange(ScanReadme(inputs.ReadmeContent));
        foreach (var workflow in inputs.GithubWorkflowContents ?? [])
        {
            mentions.AddRange(ScanGithubActions(workflow));
        }

        return mentions
            .Where(m => !IsExcluded(m.Name))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var name = group.First().Name;
                var defaultValue = group.Select(m => m.Default).FirstOrDefault(d => !string.IsNullOrEmpty(d));
                return new DetectedEnvVar(
                    name,
                    IsSecret: LooksSecret(name),
                    HasDefault: defaultValue is not null,
                    DefaultValue: defaultValue,
                    Category: Classify(name),
                    Sources: group.Select(m => m.Source).Distinct().ToArray());
            })
            // Required-and-secret first: that is what the user must act on.
            .OrderByDescending(v => v.IsSecret && !v.HasDefault)
            .ThenBy(v => v.HasDefault)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>${VAR}, ${VAR:-default}, ${VAR:?message}.</summary>
    internal static IEnumerable<(string, string?, string)> ScanCompose(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        foreach (Match match in ComposeVarRegex().Matches(content))
        {
            var defaultValue = match.Groups["def"].Success ? match.Groups["def"].Value : null;
            yield return (match.Groups["name"].Value, defaultValue, "compose");
        }
    }

    /// <summary>ARG NAME[=default] and ENV NAME=value. An ENV with a value counts as a default.</summary>
    internal static IEnumerable<(string, string?, string)> ScanDockerfile(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        foreach (Match match in DockerfileVarRegex().Matches(content))
        {
            var value = match.Groups["val"].Success ? match.Groups["val"].Value.Trim('"') : null;
            yield return (match.Groups["name"].Value, string.IsNullOrEmpty(value) ? null : value, "dockerfile");
        }
    }

    /// <summary>KEY=value lines; an empty value marks the variable as required.</summary>
    internal static IEnumerable<(string, string?, string)> ScanDotEnv(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            var match = DotEnvLineRegex().Match(trimmed);
            if (match.Success)
            {
                var value = match.Groups["val"].Value.Trim().Trim('"', '\'');
                // Placeholders ("changeme", "your-key-here") are instructions, not defaults.
                var isPlaceholder = PlaceholderRegex().IsMatch(value);
                yield return (match.Groups["name"].Value, value.Length > 0 && !isPlaceholder ? value : null, "dotenv");
            }
        }
    }

    /// <summary>
    /// Leaf keys with *empty* string values, as Section__Key. An empty appsettings value is the
    /// .NET convention for "provided by the environment at runtime"; populated values are real
    /// config, and listing them all would bury the ones that matter.
    /// </summary>
    internal static IEnumerable<(string, string?, string)> ScanAppsettings(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var name in WalkEmptyLeaves(document.RootElement, prefix: null))
            {
                yield return (name, null, "appsettings");
            }
        }
    }

    private static IEnumerable<string> WalkEmptyLeaves(JsonElement element, string? prefix)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            // Logging levels etc. are framework noise, never user-supplied runtime config.
            if (prefix is null && property.Name is "Logging" or "AllowedHosts")
            {
                continue;
            }

            var name = prefix is null ? property.Name : $"{prefix}__{property.Name}";

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in WalkEmptyLeaves(property.Value, name))
                {
                    yield return nested;
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.String &&
                     string.IsNullOrEmpty(property.Value.GetString()))
            {
                yield return name;
            }
        }
    }

    /// <summary>KEY=value lines inside fenced code blocks — the "set these" section of a README.</summary>
    internal static IEnumerable<(string, string?, string)> ScanReadme(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        var inFence = false;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```"))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                continue;
            }

            var match = DotEnvLineRegex().Match(trimmed);
            if (match.Success)
            {
                yield return (match.Groups["name"].Value, null, "readme");
            }
        }
    }

    /// <summary>${{ secrets.NAME }} references in workflows — deploy-time config by definition.</summary>
    internal static IEnumerable<(string, string?, string)> ScanGithubActions(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        foreach (Match match in ActionsSecretRegex().Matches(content))
        {
            yield return (match.Groups["name"].Value, null, "github-actions");
        }
    }

    public static bool LooksSecret(string name) => SecretNameRegex().IsMatch(name);

    /// <summary>
    /// Order matters: Storage and Database are checked before Domain, because DATABASE_URL and
    /// STORAGE_ENDPOINT would otherwise be misfiled by their URL-ish suffixes.
    /// </summary>
    public static EnvVarCategory Classify(string name)
    {
        if (StorageNameRegex().IsMatch(name))
        {
            return EnvVarCategory.Storage;
        }

        if (DatabaseNameRegex().IsMatch(name))
        {
            return EnvVarCategory.Database;
        }

        if (name.Contains("ADMIN_EMAIL", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Admin__Email", StringComparison.OrdinalIgnoreCase))
        {
            return EnvVarCategory.AdminEmail;
        }

        if (DomainNameRegex().IsMatch(name))
        {
            return EnvVarCategory.Domain;
        }

        return EnvVarCategory.Generic;
    }

    private static bool IsExcluded(string name) =>
        ExcludedNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        ExcludedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::-(?<def>[^}]*))?(?::\?[^}]*)?\}")]
    private static partial Regex ComposeVarRegex();

    [GeneratedRegex(@"^\s*(?:ARG|ENV)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:[=\s](?<val>[^\s#]+))?", RegexOptions.Multiline)]
    private static partial Regex DockerfileVarRegex();

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)=(?<val>.*)$")]
    private static partial Regex DotEnvLineRegex();

    [GeneratedRegex(@"\$\{\{\s*secrets\.(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex ActionsSecretRegex();

    [GeneratedRegex(@"(KEY|SECRET|PASSWORD|TOKEN|CREDENTIAL|PWD)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretNameRegex();

    [GeneratedRegex(@"(STORAGE_|Storage__|^S3_|^AWS_|BUCKET)", RegexOptions.IgnoreCase)]
    private static partial Regex StorageNameRegex();

    [GeneratedRegex(@"(DATABASE|ConnectionStrings|CONNECTION_STRING|^REDIS)", RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseNameRegex();

    [GeneratedRegex(@"(ORIGIN|_URL$|BASE_URL|FQDN|DOMAIN)", RegexOptions.IgnoreCase)]
    private static partial Regex DomainNameRegex();

    [GeneratedRegex(@"^(changeme|change-me|your[-_].*|<.*>|xxx+|\.\.\.|todo)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderRegex();
}
