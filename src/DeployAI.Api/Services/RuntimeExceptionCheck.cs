using System.Text.RegularExpressions;
using DeployAI.Core.Providers;
using DeployAI.Data.Entities;

namespace DeployAI.Api.Services;

/// <summary>One distinct failure the app logged, and how many times it logged it.</summary>
public sealed record RuntimeExceptionFinding(string Summary, int Count);

/// <summary>
/// What an app's own output says about whether it is working.
/// </summary>
/// <param name="Inconclusive">The log could not be read. Never the same as "found nothing" — a
/// container that cannot be reached and a container that is running cleanly produce the same
/// empty list, and only one of them is good news.</param>
public sealed record RuntimeExceptionScan(
    IReadOnlyList<RuntimeExceptionFinding> Findings,
    bool Inconclusive,
    string? Reason);

public interface IRuntimeExceptionCheck
{
    Task<RuntimeExceptionScan> ScanAsync(DeployTarget target, int lines, CancellationToken cancellationToken);
}

/// <summary>
/// Reads what the application itself printed and reports the failures in it.
/// </summary>
/// <remarks>
/// The generic answer to a specific hole: a deploy can go green on every check DeployAI makes
/// while the app is broken for everyone using it. yemenConnect's <c>/public/stats</c> — the endpoint
/// its landing page calls on every visit — returned 500 on every request for the entire life of the
/// deployment. The build was green, the migration chain valid, <c>/health</c> returned 200, both
/// targets reported success, and the only trace of it anywhere was in the container's own output.
/// <para>
/// Probing routes cannot close this generally. The app's OpenAPI document sits behind the same
/// fallback authorization policy as everything else, so there is nothing to enumerate from outside,
/// and guessing paths produces confident wrong answers. The app's log needs no such guessing: an
/// application that is failing says so, in its own words, whatever language it is written in.
/// </para>
/// <para>
/// Advisory, like every other post-deploy check here. An app can log an error and be perfectly
/// healthy. Reporting is the point — a line a person reads is what turned every silent failure in
/// this codebase into a fixed one.
/// </para>
/// </remarks>
public sealed class RuntimeExceptionCheck : IRuntimeExceptionCheck
{
    private readonly IProviderRuntimeLogsFactory _runtimeLogsFactory;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly ILogger<RuntimeExceptionCheck> _logger;

    public RuntimeExceptionCheck(
        IProviderRuntimeLogsFactory runtimeLogsFactory,
        IProviderCredentialTokenService tokens,
        ILogger<RuntimeExceptionCheck> logger)
    {
        _runtimeLogsFactory = runtimeLogsFactory;
        _tokens = tokens;
        _logger = logger;
    }

    public async Task<RuntimeExceptionScan> ScanAsync(
        DeployTarget target,
        int lines,
        CancellationToken cancellationToken)
    {
        var runtimeLogs = _runtimeLogsFactory.GetRuntimeLogs(target.ProviderName);
        if (runtimeLogs is null)
        {
            return new RuntimeExceptionScan([], Inconclusive: true,
                $"{target.ProviderName} does not expose container output");
        }

        if (string.IsNullOrWhiteSpace(target.ProviderProjectId))
        {
            return new RuntimeExceptionScan([], Inconclusive: true, "this app has no container yet");
        }

        string logs;
        try
        {
            var token = await _tokens.GetTokenAsync(target.Credential, cancellationToken);
            logs = await runtimeLogs.GetRuntimeLogsAsync(
                new ProviderCredentials(token), target.ProviderProjectId!, lines, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The common case is a stopped container, which is exactly the state worth naming:
            // an app that is not running is not an app with a clean log.
            _logger.LogInformation(ex, "Could not read container output for target {TargetId}.", target.Id);
            return new RuntimeExceptionScan([], Inconclusive: true, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(logs))
        {
            return new RuntimeExceptionScan([], Inconclusive: true, "the container returned no output");
        }

        return new RuntimeExceptionScan(RuntimeExceptionPatterns.Find(logs), Inconclusive: false, null);
    }
}

/// <summary>
/// Finds application failures in raw container output, without knowing what the application is.
/// </summary>
/// <remarks>
/// Every pattern here is matched against output a real deployment actually produced, not against a
/// guess at the format — the first draft of this flagged ASP.NET's <c>warn:</c> lines, of which a
/// healthy yemenConnect logs four at startup (data protection, EF model validation). A check that
/// cries wolf on a working app is worse than no check, because the one time it is right nobody
/// looks.
/// </remarks>
public static partial class RuntimeExceptionPatterns
{
    /// <summary>How far past a header to look for the line naming the exception.</summary>
    private const int DetailWindow = 15;

    /// <summary>Keeps one long message from filling the deploy log.</summary>
    private const int MaxSummaryLength = 300;

    public static IReadOnlyList<RuntimeExceptionFinding> Find(string logs)
    {
        if (string.IsNullOrWhiteSpace(logs))
        {
            return [];
        }

        var lines = logs.Replace("\r\n", "\n").Split('\n');
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsFailureHeader(lines, i))
            {
                continue;
            }

            var summary = SummariseFrom(lines, i);
            if (!counts.TryAdd(summary, 1))
            {
                counts[summary]++;
            }
            else
            {
                order.Add(summary);
            }
        }

        return order.Select(s => new RuntimeExceptionFinding(s, counts[s])).ToList();
    }

    /// <summary>
    /// Whether this line is an application saying something failed.
    /// </summary>
    private static bool IsFailureHeader(string[] lines, int index)
    {
        var line = lines[index];

        // .NET's console logger prefixes by level: "fail:" is Error and "crit:" is Critical, while
        // "warn:", "info:" and "dbug:" are not failures. Level, not text matching -- searching for
        // the word "Exception" would catch a logger category and miss a plain Error.
        if (line.StartsWith("fail: ", StringComparison.Ordinal) ||
            line.StartsWith("crit: ", StringComparison.Ordinal))
        {
            return true;
        }

        // Python prints this exact header before every unhandled traceback.
        if (line.Contains("Traceback (most recent call last)", StringComparison.Ordinal))
        {
            return true;
        }

        // Go's runtime, and anything using the convention.
        if (line.StartsWith("panic: ", StringComparison.Ordinal) ||
            line.StartsWith("fatal error: ", StringComparison.Ordinal))
        {
            return true;
        }

        // Node names these explicitly when it has them.
        if (line.Contains("UnhandledPromiseRejection", StringComparison.Ordinal) ||
            line.Contains("Uncaught Exception", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A bare Node stack: "TypeError: x is not a function" at column 0, with a frame under it.
        // The following "    at " is what makes this a stack rather than a line of prose that
        // happens to contain a colon.
        if (JsErrorHeader().IsMatch(line) &&
            index + 1 < lines.Length &&
            lines[index + 1].TrimStart().StartsWith("at ", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// The most identifying line available: the exception type and its message where there is one,
    /// falling back to the header. Grouping is by this string, so two calls that failed the same way
    /// count as one finding rather than filling the log.
    /// </summary>
    private static string SummariseFrom(string[] lines, int index)
    {
        var limit = Math.Min(lines.Length, index + DetailWindow + 1);
        for (var i = index; i < limit; i++)
        {
            var match = TypedFailure().Match(lines[i]);
            if (match.Success)
            {
                var message = match.Groups[2].Value.Trim() + ContinuationAfter(lines, i);
                return Truncate($"{match.Groups[1].Value}: {message}");
            }
        }

        // A failure with no typed line under it -- still worth reporting, and the header carries
        // the logger category, which names the part of the app that failed.
        return Truncate(lines[index].Trim());
    }

    /// <summary>
    /// The rest of a message that wrapped onto following lines, joined back into one.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. EF Core's translation failures put the query on the first line and the reason
    /// on a later one, so the first real run of this check reported
    /// <c>System.InvalidOperationException: The LINQ expression 'DbSet&lt;Member&gt;()</c> and stopped —
    /// naming the exception but not the thing a person could act on, which was
    /// "Translation of member 'IsDeleted' … failed".
    /// </remarks>
    private static string ContinuationAfter(string[] lines, int index)
    {
        var parts = new List<string>();

        for (var i = index + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // A stack frame ends the message, as does a new log entry or a blank line.
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("at ", StringComparison.Ordinal) ||
                trimmed.StartsWith("--- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("File \"", StringComparison.Ordinal) ||
                LogLevelPrefix().IsMatch(line))
            {
                break;
            }

            parts.Add(trimmed);
            if (parts.Sum(p => p.Length + 1) > MaxSummaryLength)
            {
                break;
            }
        }

        return parts.Count == 0 ? string.Empty : " " + string.Join(' ', parts);
    }

    private static string Truncate(string value) =>
        value.Length <= MaxSummaryLength ? value : value[..MaxSummaryLength].TrimEnd() + "…";

    /// <summary>
    /// A type name ending in Exception or Error, followed by its message. Covers .NET
    /// ("System.InvalidOperationException: …"), Python ("ValueError: …") and JavaScript
    /// ("TypeError: …") without needing to know which one produced the log.
    /// </summary>
    [GeneratedRegex(@"^\s*([A-Za-z_][\w.]*(?:Exception|Error))\s*:\s*(\S.*)$", RegexOptions.Compiled)]
    private static partial Regex TypedFailure();

    [GeneratedRegex(@"^[A-Za-z_][\w.]*(?:Exception|Error)\s*:\s*\S", RegexOptions.Compiled)]
    private static partial Regex JsErrorHeader();

    /// <summary>The start of a new .NET console log entry, which ends the previous message.</summary>
    [GeneratedRegex(@"^(?:trce|dbug|info|warn|fail|crit)\s*:\s", RegexOptions.Compiled)]
    private static partial Regex LogLevelPrefix();
}
