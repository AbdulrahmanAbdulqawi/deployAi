using System.Text.RegularExpressions;
using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

/// <inheritdoc cref="IMissingConfigurationDetector" />
public sealed partial class MissingConfigurationDetector : IMissingConfigurationDetector
{
    private const int MaxResults = 10;

    /// <summary>
    /// Names that appear in these messages but are never the thing to set. "Configuration" and
    /// "Environment" are the words the message is built from; the rest are runtime-injected.
    /// </summary>
    private static readonly HashSet<string> NotAskable = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration", "Environment", "Settings", "Value", "The", "This", "A", "An",
        "PATH", "HOME", "PORT", "TZ", "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "NODE_ENV"
    };

    public IReadOnlyList<MissingConfiguration> Detect(IReadOnlyList<string> logLines)
    {
        var found = new Dictionary<string, MissingConfiguration>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in logLines ?? [])
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            foreach (var (match, kind) in MatchesIn(line))
            {
                var name = match.Groups["name"].Value.Trim().Trim('\'', '"', '`');
                if (name.Length == 0 || NotAskable.Contains(name) || found.ContainsKey(name))
                {
                    continue;
                }

                // A name that already carries a separator is a full key however it was phrased --
                // "Jwt__Key configuration missing" names the key, not the section.
                var resolved = kind == MissingConfigurationKind.ConfigurationSection
                    && (name.Contains("__", StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal))
                        ? MissingConfigurationKind.EnvironmentVariable
                        : kind;

                found[name] = new MissingConfiguration(name, resolved, line.Trim());

                if (found.Count >= MaxResults)
                {
                    return found.Values.ToArray();
                }
            }
        }

        return found.Values.ToArray();
    }

    private static IEnumerable<(Match Match, MissingConfigurationKind Kind)> MatchesIn(string line)
    {
        // .NET's conventional phrasing names a section: "Jwt configuration missing".
        foreach (Match m in SectionRegex().Matches(line))
        {
            yield return (m, MissingConfigurationKind.ConfigurationSection);
        }

        foreach (Match m in EnvVarRegex().Matches(line))
        {
            yield return (m, MissingConfigurationKind.EnvironmentVariable);
        }

        foreach (Match m in QuotedConfigRegex().Matches(line))
        {
            yield return (m, MissingConfigurationKind.ConfigurationSection);
        }
    }

    /// <summary>"Jwt configuration missing", "Smtp configuration is missing".</summary>
    [GeneratedRegex(
        @"(?<name>[A-Za-z][A-Za-z0-9_.:]*)\s+configuration\s+(?:is\s+|was\s+)?(?:missing|not\s+found|not\s+configured|required)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SectionRegex();

    /// <summary>
    /// "Missing required environment variable: API_KEY", "Environment variable 'API_KEY' is
    /// required", "API_KEY is not defined". Upper-snake only: lowercase words in this shape are
    /// nearly always prose, and a wrong suggestion costs more than a missed one here.
    /// </summary>
    [GeneratedRegex(
        @"(?:(?:missing|required|undefined)\s+(?:required\s+)?env(?:ironment)?\s+variable[:\s]+'?""?(?<name>[A-Z][A-Z0-9_]{2,})'?""?)"
        + @"|(?:env(?:ironment)?\s+variable\s+'?""?(?<name>[A-Z][A-Z0-9_]{2,})'?""?\s+(?:is\s+)?(?:required|missing|not\s+set|undefined))"
        + @"|(?:'?""?(?<name>[A-Z][A-Z0-9_]{2,})'?""?\s+is\s+not\s+defined)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnvVarRegex();

    /// <summary>"Configuration value 'Jwt:Key' is missing", "Config key \"Smtp__Host\" not found".</summary>
    [GeneratedRegex(
        @"(?:config(?:uration)?\s+(?:value|key|setting)\s+['""`](?<name>[A-Za-z][A-Za-z0-9_.:]*)['""`]\s*(?:is\s+)?(?:missing|not\s+found|required))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedConfigRegex();
}
