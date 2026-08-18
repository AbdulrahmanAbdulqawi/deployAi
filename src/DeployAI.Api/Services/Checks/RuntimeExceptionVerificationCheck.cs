using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services.Checks;

/// <summary>
/// Reads what each app is actually saying, and fails the check when it is saying it is broken.
/// </summary>
/// <remarks>
/// <para>
/// The scan itself already existed and already worked — it runs before and after every deploy — but
/// its findings only ever reached the deploy log, where nobody was looking an hour later. That is the
/// gap this closes: yemenConnect's <c>/public/stats</c>, the endpoint its landing page calls on every
/// visit, returned 500 on every request for the entire life of a deployment. The build was green, the
/// migrations valid, <c>/health</c> returned 200, both targets reported success, and the only trace
/// anywhere was in the container's own output.
/// </para>
/// <para>
/// Only Coolify exposes container output, so on every other provider this reports
/// <see cref="VerificationCheckStatus.Skipped"/> with the reason named. Reporting a silent pass there
/// would be the worst of both worlds: a check that looks like evidence and is not.
/// </para>
/// </remarks>
public sealed class RuntimeExceptionVerificationCheck : IProjectCheckContributor
{
    private readonly IRuntimeExceptionCheck _scan;
    private readonly IProviderRuntimeLogsFactory _runtimeLogsFactory;
    private readonly FleetVerificationOptions _options;

    public RuntimeExceptionVerificationCheck(
        IRuntimeExceptionCheck scan,
        IProviderRuntimeLogsFactory runtimeLogsFactory,
        IOptions<FleetVerificationOptions> options)
    {
        _scan = scan;
        _runtimeLogsFactory = runtimeLogsFactory;
        _options = options.Value;
    }

    public string Name => "runtime";

    public async Task<IReadOnlyList<ProjectVerificationCheck>> ContributeAsync(
        ProjectCheckContext context,
        CancellationToken cancellationToken)
    {
        var checks = new List<ProjectVerificationCheck>();

        foreach (var target in context.DeployTargets)
        {
            var config = DeployTargetConfig.Parse(target.ConfigJson);
            if (!config.IsDeployableTarget)
            {
                continue;
            }

            var checkId = $"runtime.exceptions:{target.Id}";
            var label = $"What the {Describe(config)} is logging";

            // Asked here rather than inferred from the scan's reason string, so "this provider has
            // no logs to read" stays a skip and never gets confused with "the logs could not be read".
            if (_runtimeLogsFactory.GetRuntimeLogs(target.ProviderName) is null)
            {
                checks.Add(Check(checkId, label, VerificationCheckStatus.Skipped, target,
                    $"{target.ProviderName} does not expose container output, so DeployAI cannot read what this app logs."));
                continue;
            }

            var scan = await _scan.ScanAsync(target, _options.RuntimeLogLines, cancellationToken);

            if (scan.Inconclusive)
            {
                checks.Add(Check(checkId, label, VerificationCheckStatus.Inconclusive, target,
                    $"DeployAI could not read this app's output ({scan.Reason ?? "no reason given"})."));
                continue;
            }

            if (scan.Findings.Count == 0)
            {
                checks.Add(Check(checkId, label, VerificationCheckStatus.Passed, target,
                    "The app's own output shows no failures."));
                continue;
            }

            checks.Add(Check(checkId, label, VerificationCheckStatus.Failed, target, Describe(scan.Findings)));
        }

        return checks;
    }

    /// <summary>
    /// Names the failures the app reported, most-repeated first, capped at three.
    /// </summary>
    /// <remarks>
    /// The count matters more than the list: one exception at startup is noise, and the same
    /// exception four hundred times is an endpoint that is down for everyone using it.
    /// </remarks>
    private static string Describe(IReadOnlyList<RuntimeExceptionFinding> findings)
    {
        var top = findings.OrderByDescending(f => f.Count).Take(3)
            .Select(f => f.Count > 1 ? $"{f.Summary} (×{f.Count})" : f.Summary);

        var extra = findings.Count > 3 ? $" …and {findings.Count - 3} more." : string.Empty;
        return $"The app logged failures: {string.Join("; ", top)}.{extra}";
    }

    private static string Describe(DeployTargetConfig config) => config.Role switch
    {
        DeploymentPartRoles.Website => "website",
        DeploymentPartRoles.Server => "API",
        _ => "app"
    };

    private static ProjectVerificationCheck Check(
        string checkId,
        string label,
        VerificationCheckStatus status,
        DeployTarget target,
        string message) =>
        new(checkId, VerificationCheckTargets.Runtime, label, status, message, null, null, target.Id);
}
