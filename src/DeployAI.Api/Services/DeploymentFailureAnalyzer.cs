using System.Text.RegularExpressions;
using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

/// <summary>Pattern-matches build/deploy log lines against known compiler/build-tool error markers (tsc, Angular, MSBuild, NuGet, etc.) to classify a failure as CodeBuild vs. Infrastructure and extract referenced file paths.</summary>
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
        "Cannot find package",
        "npm ERR!",
        "npm error",
        "ERR_PNPM",
        "ELIFECYCLE",
        "error during build",
        "Build error occurred",
        "Compilation failed",
        "SyntaxError:",
        "Type error:",
        "Build failed",
        "Could not resolve",
        "Rollup failed to resolve",
        "Transform failed",
        // Deliberately not a bare "esbuild": Angular builds *with* esbuild, so it names itself
        // in the log of every successful build. Matching the tool's name rather than an error
        // from it classified every green Angular deployment as a code failure.
        "esbuild: error",
        "[esbuild] Error",
        "Command failed with exit code",
        "command not found",
        "exited with 1",
        "[ERROR]",
        "\u2718",
        "Application bundle generation failed"
    ];

    private static readonly string[] DockerBuildMarkers =
    [
        "dockerfile invalid",
        "invalid dockerfile",
        "failed to solve",
        "error building docker",
        "docker build failed"
    ];

    private static readonly string[] HardInfrastructureMarkers =
    [
        "not linked to GitHub",
        "Reconnect it in settings",
        "Cross-provider environment sync",
        "rate limit",
        "unauthorized",
        "gitHub_auth",
        "provider_token_invalid",
        "invalid_credential",
        // Railway opaque builder/image failures with no actionable compiler output.
        "Failed to build an image",
        "Please check the build logs for more details"
    ];

    private static readonly string[] GenericFailureMarkers =
    [
        "Something went wrong while publishing",
        "Publishing did not go through",
        "Waiting for activity from Vercel"
    ];

    // Vercel/config failures that carry no compiler output but are typically fixable by editing
    // repository config (usually vercel.json), e.g. an env var pointing at a Secret that was
    // never created, or an invalid vercel.json schema. These must be matched explicitly because
    // none of them contain a build-error marker or a compiler diagnostic line.
    private static readonly string[] FixableConfigMarkers =
    [
        "references Secret",
        "references a secret",
        "Invalid vercel.json",
        "should NOT have additional property",
        "Function Runtimes must have a valid version",
        "cannot be used in conjunction with",
        "has invalid `source` pattern",
        "Header at index"
    ];

    private static readonly Regex TypeScriptPathRegex = new(
        @"(?<path>[^\s:(]+?\.(?:ts|tsx|js|jsx|mjs|cjs)):(?<line>\d+)(?::\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CSharpPathRegex = new(
        @"(?<path>[^\s(]+?\.cs)\((?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WarningLineRegex = new(
        @"(^\s*warning[\s\[]|: warning |warning CS|warning TS|warning NG|warning MSB|warning NU|\bnpm warn\b|\bWARN(?:ING)?:|\[WARNING\]|\bWarning\b.*\bCS\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Matches both the classic tsc format ("... - error TS2304: ...") and the esbuild/Angular
    // diagnostic format ("\u2718 [ERROR] TS2339: ..."), where "error" is bracketed and the code is
    // not contiguous with the word "error".
    private static readonly Regex ErrorLineRegex = new(
        @"(\berror TS|\berror NG|\berror CS|\berror MSB|\berror NU|: error |\(error |\[ERROR\]|\u2718|\bTS\d{3,}\b|\bNG\d{2,}\b|npm ERR!|npm error|ERR_PNPM|ELIFECYCLE|Failed to compile|Compilation failed|SyntaxError:|Type error:|BUILD FAILED|Module not found|Cannot find module|Cannot find package|Could not resolve|Rollup failed to resolve|Transform failed|Command failed with exit code|command not found|Build error occurred|Build failed|\bFAILED\b)",
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
        var hardInfrastructureHit = HardInfrastructureMarkers.FirstOrDefault(marker =>
            joined.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var codeHit = CodeBuildMarkers.FirstOrDefault(marker =>
            joined.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var dockerHit = DockerBuildMarkers.FirstOrDefault(marker =>
            joined.Contains(marker, StringComparison.OrdinalIgnoreCase));

        var errorLines = SelectErrorLines(logLines);
        var errorCount = logLines.Count(IsErrorOrFailureLine);
        var referencedFiles = ExtractReferencedFiles(errorLines);

        // Genuine infrastructure/auth problems cannot be resolved by editing repository code.
        if (!string.IsNullOrWhiteSpace(hardInfrastructureHit) && string.IsNullOrWhiteSpace(codeHit))
        {
            var infraExcerpt = BuildExcerpt(
                ResolveInfraExcerptLines(logLines, HardInfrastructureMarkers, errorLines),
                maxExcerptLines,
                maxExcerptChars);
            return new DeploymentFailureAnalysis(
                DeploymentFailureCategory.Infrastructure,
                SummarizeInfrastructure(hardInfrastructureHit),
                infraExcerpt,
                referencedFiles,
                false,
                errorCount);
        }

        // A recognized build marker OR any concrete error output means Claude can attempt a fix.
        // The error-line fallback covers Vite/esbuild/Rollup/Angular client builds whose exact
        // wording is not in the marker list (e.g. a bare stack trace ending in "FAILED").
        if (!string.IsNullOrWhiteSpace(codeHit) || !string.IsNullOrWhiteSpace(dockerHit) || errorLines.Count > 0)
        {
            var excerpt = BuildExcerpt(
                errorLines.Count > 0 ? errorLines : logLines.TakeLast(maxExcerptLines).ToList(),
                maxExcerptLines,
                maxExcerptChars);
            var fixMarker = codeHit ?? dockerHit ?? "build error";
            var fixFiles = referencedFiles.Count > 0
                ? referencedFiles
                : !string.IsNullOrWhiteSpace(dockerHit)
                    ? (IReadOnlyList<string>)["Dockerfile"]
                    : referencedFiles;
            return new DeploymentFailureAnalysis(
                DeploymentFailureCategory.CodeBuild,
                SummarizeCodeBuild(fixMarker, fixFiles, errorCount),
                excerpt,
                fixFiles,
                !string.IsNullOrWhiteSpace(excerpt),
                errorCount);
        }

        // Repo-fixable Vercel/config errors (e.g. an env var referencing a missing Secret). These
        // have no compiler output, so they are matched explicitly and routed to the fix pipeline
        // with a vercel.json hint so the fix agent knows where to look.
        var configHit = FixableConfigMarkers.FirstOrDefault(marker =>
            joined.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(configHit))
        {
            var configLines = logLines
                .Where(line => FixableConfigMarkers.Any(marker =>
                    line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var configExcerpt = BuildExcerpt(
                configLines.Count > 0 ? configLines : logLines.TakeLast(maxExcerptLines).ToList(),
                maxExcerptLines,
                maxExcerptChars);
            var configFiles = referencedFiles.Count > 0 ? referencedFiles : ["vercel.json"];
            return new DeploymentFailureAnalysis(
                DeploymentFailureCategory.CodeBuild,
                SummarizeConfiguration(configHit),
                configExcerpt,
                configFiles,
                !string.IsNullOrWhiteSpace(configExcerpt),
                errorCount);
        }

        // Only low-signal, generic failure messages were captured — surface the failure but do
        // not offer an automated code fix, since there is no build output to act on.
        var genericHit = GenericFailureMarkers.FirstOrDefault(marker =>
            joined.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var fallbackExcerpt = BuildExcerpt(
            logLines.TakeLast(maxExcerptLines).ToList(),
            maxExcerptLines,
            maxExcerptChars);
        return new DeploymentFailureAnalysis(
            string.IsNullOrWhiteSpace(genericHit)
                ? DeploymentFailureCategory.Unknown
                : DeploymentFailureCategory.Infrastructure,
            string.IsNullOrWhiteSpace(genericHit)
                ? "The deployment failed, but the logs do not show a clear code or build error."
                : SummarizeInfrastructure(genericHit),
            fallbackExcerpt,
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

    private const int MaxForwardContextLines = 8;

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

            // The esbuild/Angular diagnostic format puts the file location and code frame on the
            // lines *after* the error header (e.g. "    src/app/foo.ts:65:34:"). Capture those so
            // the excerpt is actionable and referenced-file extraction can see the paths.
            var contextCount = 0;
            for (var j = i + 1; j < logLines.Count && contextCount < MaxForwardContextLines; j++)
            {
                var next = logLines[j];
                if (IsErrorOrFailureLine(next) || !IsDiagnosticContinuationLine(next))
                {
                    break;
                }

                if (!IsWarningLine(next) && !selected.Contains(next, StringComparer.Ordinal))
                {
                    selected.Add(next);
                }

                contextCount++;
            }
        }

        return selected;
    }

    private static bool IsDiagnosticContinuationLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        // Indented continuation lines (file location and code frame) or a bare "path:line:col".
        return char.IsWhiteSpace(line[0]) ||
               TypeScriptPathRegex.IsMatch(line) ||
               CSharpPathRegex.IsMatch(line);
    }

    private static IReadOnlyList<string> ResolveInfraExcerptLines(
        IReadOnlyList<string> logLines,
        IReadOnlyList<string> markers,
        IReadOnlyList<string> errorLines)
    {
        var infrastructureLines = logLines
            .Where(line => markers.Any(marker =>
                line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (infrastructureLines.Count > 0)
        {
            return infrastructureLines;
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

    private static string SummarizeConfiguration(string marker)
    {
        if (marker.Contains("Secret", StringComparison.OrdinalIgnoreCase))
        {
            return "Deployment failed: an environment variable references a Vercel Secret that does not exist. This is usually fixed by removing the '@secret' reference in vercel.json (use a plain value or a normal Environment Variable).";
        }

        if (marker.Contains("invalid `source` pattern", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("Header at index", StringComparison.OrdinalIgnoreCase))
        {
            return "Deployment failed: vercel.json has an invalid headers source pattern. Vercel header sources do not support regex alternation like .(js|css|...) — use separate header rules per extension or remove the headers block.";
        }

        return "Deployment failed due to an invalid Vercel configuration (vercel.json).";
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
            var m when m.Contains("Failed to build an image", StringComparison.OrdinalIgnoreCase) ||
                       m.Contains("check the build logs", StringComparison.OrdinalIgnoreCase) =>
                "Railway failed to build the container image and did not return actionable build logs. Try Redeploy, or inspect the build in the Railway dashboard.",
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
