using System.Text;
using System.Text.RegularExpressions;

namespace DeployAI.Api.Services;

internal static class BuildErrorActionHints
{
    private static readonly Regex MissingAngularProjectPropertyRegex = new(
        @"Project\s+""(?<project>[^""]+)""\s+is\s+missing\s+a\s+required\s+property\s+""(?<property>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ConfigFileLoadRegex = new(
        @"(?:Workspace config file cannot be loaded|Cannot read(?: config file)?):\s*(?<path>\S+angular\.json)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string? BuildSection(string? errorExcerpt)
    {
        if (string.IsNullOrWhiteSpace(errorExcerpt))
        {
            return null;
        }

        var hints = new List<string>();

        foreach (Match match in MissingAngularProjectPropertyRegex.Matches(errorExcerpt))
        {
            var project = match.Groups["project"].Value;
            var property = match.Groups["property"].Value;
            var valueHint = property.Equals("root", StringComparison.OrdinalIgnoreCase)
                ? "\"root\": \"\""
                : $"\"{property}\": <valid value>";

            hints.Add(
                $"In angular.json, add only the missing property {valueHint} on project \"{project}\". " +
                "Do not change outputPath, assets, styles, budgets, fileReplacements, or other unrelated fields.");
        }

        foreach (Match match in ConfigFileLoadRegex.Matches(errorExcerpt))
        {
            hints.Add(
                $"Read {match.Groups["path"].Value} from GitHub and fix only the validation error described above. " +
                "Do not rewrite unrelated sections of the file.");
        }

        if (hints.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("## Required fixes (derived from build errors)");
        foreach (var hint in hints.Distinct(StringComparer.Ordinal))
        {
            builder.AppendLine($"- {hint}");
        }

        return builder.ToString().TrimEnd();
    }
}
