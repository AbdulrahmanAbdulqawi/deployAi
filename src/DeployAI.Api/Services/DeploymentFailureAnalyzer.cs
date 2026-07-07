using System.Text.RegularExpressions;
using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

public sealed class DeploymentFailureAnalyzer : IDeploymentFailureAnalyzer
{
    private const int MaxExcerptChars = 16384;
    private const int MaxExcerptLines = 120;
    private const int MaxFixExcerptChars = 65536;
    private const int MaxFixExcerptLines = 300;
    private const int MaxReferencedFiles = 25;

    private static readonly string[] CodeBuildMarkers =
    [
        "error TS",
        "error NG",
        "error CS",
        "error MSB",
        "error NU",
        "BUILD FAILED",
        "Failed to compile",
        "Module not found",
        "Cannot find module",
        "npm ERR!",
        "error during build",
        "Compilation failed",
        "SyntaxError:",
        "Type error:",
        "Build failed"
    ];

    private static readonly string[] InfrastructureMarkers =
    [
        "not linked to GitHub",
        "Reconnect it in settings",
        "Cross-provider environment sync",
        "rate limit",
        "unauthorized",
        "gitHub_auth",
        "provider_token_invalid",
        "invalid_credential",
        "Something went wrong while publishing",
        "Publishing did not go through",
        "Waiting for activity from Vercel"
    ];

    private static readonly Regex TypeScriptPathRegex = new(
        @"(?<path>[^\s:(]+?\.(?:ts|tsx|js|jsx|mjs|cjs)):(?<line>\d+)(?::\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CSharpPathRegex = new(
        @"(?<path>[^\s(]+?\.cs)\((?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WarningLineRegex = new(
        @"(^\s*warning[\s\[]|: warning |warning CS|warning TS|warning NG|warning MSB|warning NU|\bnpm warn\b|\bWARN(?:ING)?:|\bWarning\b.*\bCS\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ErrorLineRegex = new(
        @"(\berror TS|\berror NG|\berror CS|\berror MSB|\berror NU|: error |\(error |npm ERR!|Failed to compile|Compilation failed|SyntaxError:|Type error:|BUILD FAILED|Module not found|Cannot find module|Build failed|\bFAILED\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public DeploymentFailureAnalysis Analyze(string providerName, IReadOnlyList<string> logLines) =>
        AnalyzeInternal(providerName, logLines, MaxExcerptLines, MaxExcerptChars);

    public DeploymentFailureAnalysis AnalyzeForFix(string providerName, IReadOnlyList<string> logLines) =>
        AnalyzeInternal(providerName, logLines, MaxFixExcerptLines, MaxFixExcerptChars);

    private DeploymentFailureAnalysis AnalyzeInternal(
        string providerName,
        IReadOnlyList<string> logLines,
        int maxExcerptLines,
        int maxExcerptChars)
    {
        _ = providerName;
        if (logLines.Count == 0)
        {
            return new DeploymentFailureAnalysis(
                DeploymentFailureCategory.Unknown,
                "No build logs were captured for this deployment.",
                null,
                [],
                false);
        }

        var joined = string.Join('\n', logLines);
        var infrastructureHit = InfrastructureMarkers.FirstOrDefault(marker =>
            joined.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var codeHit = CodeBuildMarkers.FirstOrDefault(marker =>
            joined.Contains(marker, StringComparison.OrdinalIgnoreCase));

        var errorLines = SelectErrorLines(logLines);
        var errorCount = logLines.Count(IsErrorOrFailureLine);
        var referencedFiles = ExtractReferencedFiles(errorLines);
        var excerpt = BuildExcerpt(
            ResolveExcerptLines(logLines, codeHit, infrastructureHit, errorLines),
            maxExcerptLines,
            maxExcerptChars);

        if (!string.IsNullOrWhiteSpace(infrastructureHit) && string.IsNullOrWhiteSpace(codeHit))
        {
            return new DeploymentFailureAnalysis(
                DeploymentFailureCategory.Infrastructure,
                SummarizeInfrastructure(infrastructureHit),
                excerpt,
                referencedFiles,
                false,
                errorCount);
        }

        if (!string.IsNullOrWhiteSpace(codeHit))
        {
            return new DeploymentFailureAnalysis(
                DeploymentFailureCategory.CodeBuild,
                SummarizeCodeBuild(codeHit, referencedFiles, errorCount),
                excerpt,
                referencedFiles,
                !string.IsNullOrWhiteSpace(excerpt),
                errorCount);
        }

        return new DeploymentFailureAnalysis(
            DeploymentFailureCategory.Unknown,
            "The deployment failed, but the logs do not show a clear code or build error.",
            excerpt,
            referencedFiles,
            false,
            errorCount);
    }

    internal static bool IsWarningLine(string line) =>
        !string.IsNullOrWhiteSpace(line) && WarningLineRegex.IsMatch(line);

    internal static bool IsErrorOrFailureLine(string line) =>
        !string.IsNullOrWhiteSpace(line) &&
        !IsWarningLine(line) &&
        ErrorLineRegex.IsMatch(line);

    private static List<string> SelectErrorLines(IReadOnlyList<string> logLines)
    {
        var selected = new List<string>();
        for (var i = 0; i < logLines.Count; i++)
        {
            var line = logLines[i];
            if (!IsErrorOrFailureLine(line))
            {
                continue;
            }

            if (i > 0)
            {
                var previous = logLines[i - 1];
                if (!IsWarningLine(previous) &&
                    !IsErrorOrFailureLine(previous) &&
                    !selected.Contains(previous, StringComparer.Ordinal))
                {
                    selected.Add(previous);
                }
            }

            if (!selected.Contains(line, StringComparer.Ordinal))
            {
                selected.Add(line);
            }
        }

        return selected;
    }

    private static IReadOnlyList<string> ResolveExcerptLines(
        IReadOnlyList<string> logLines,
        string? codeHit,
        string? infrastructureHit,
        IReadOnlyList<string> errorLines)
    {
        if (!string.IsNullOrWhiteSpace(codeHit) && errorLines.Count > 0)
        {
            return errorLines;
        }

        if (!string.IsNullOrWhiteSpace(infrastructureHit))
        {
            var infrastructureLines = logLines
                .Where(line => InfrastructureMarkers.Any(marker =>
                    line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (infrastructureLines.Count > 0)
            {
                return infrastructureLines;
            }
        }

        return errorLines.Count > 0 ? errorLines : logLines.TakeLast(MaxExcerptLines).ToList();
    }

    private static string SummarizeCodeBuild(string marker, IReadOnlyList<string> referencedFiles, int errorCount)
    {
        if (errorCount > 1)
        {
            if (referencedFiles.Count > 1)
            {
                return $"Build failed with {errorCount} errors across {referencedFiles.Count} files ({marker.Trim()}).";
            }

            return $"Build failed with {errorCount} errors ({marker.Trim()}).";
        }

        if (referencedFiles.Count > 0)
        {
            return $"Build failed ({marker.Trim()}) in {referencedFiles[0]}.";
        }

        return $"Build failed with a code error ({marker.Trim()}).";
    }

    private static string SummarizeInfrastructure(string marker) =>
        marker switch
        {
            var m when m.Contains("GitHub", StringComparison.OrdinalIgnoreCase) =>
                "Deployment failed due to a GitHub or repository link issue.",
            var m when m.Contains("rate limit", StringComparison.OrdinalIgnoreCase) =>
                "Deployment failed because a provider rate limit was hit.",
            var m when m.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                       m.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) =>
                "Deployment failed due to a hosting connection or permission issue.",
            _ => "Deployment failed due to an infrastructure or configuration issue."
        };

    private static IReadOnlyList<string> ExtractReferencedFiles(IReadOnlyList<string> logLines)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in logLines)
        {
            foreach (Match match in TypeScriptPathRegex.Matches(line))
            {
                var path = NormalizePath(match.Groups["path"].Value);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }

            foreach (Match match in CSharpPathRegex.Matches(line))
            {
                var path = NormalizePath(match.Groups["path"].Value);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }
        }

        return paths.Take(MaxReferencedFiles).ToArray();
    }

    private static string? BuildExcerpt(
        IReadOnlyList<string> errorLines,
        int maxExcerptLines,
        int maxExcerptChars)
    {
        if (errorLines.Count == 0)
        {
            return null;
        }

        var lines = errorLines.Count > maxExcerptLines
            ? errorLines.Take(maxExcerptLines).ToList()
            : errorLines;

        var excerpt = string.Join(Environment.NewLine, lines).Trim();
        if (errorLines.Count > maxExcerptLines)
        {
            excerpt += Environment.NewLine +
                       $"[... {errorLines.Count - maxExcerptLines} additional error lines omitted from excerpt ...]";
        }

        if (excerpt.Length > maxExcerptChars)
        {
            excerpt = excerpt[..maxExcerptChars] + Environment.NewLine +
                      $"[... excerpt truncated to {maxExcerptChars} characters; {errorLines.Count} error lines total ...]";
        }

        return string.IsNullOrWhiteSpace(excerpt) ? null : excerpt;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');
}
