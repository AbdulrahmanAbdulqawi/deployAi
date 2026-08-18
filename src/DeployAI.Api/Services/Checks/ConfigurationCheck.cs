using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services.Checks;

/// <summary>
/// Keeps asking whether the running app still has the settings the deployed code needs.
/// </summary>
/// <remarks>
/// <para>
/// Missing, wrong, and drifted environment variables cause more deployment failures than anything
/// Docker-shaped, and DeployAI already computed the answer — at deploy time, once, into a log line.
/// A key deleted in the provider's UI three weeks later was invisible forever.
/// </para>
/// <para>
/// The expensive half of that answer, reading the repository, is already recorded in
/// <see cref="TargetConfigManifest"/>, so this costs no GitHub calls at all: it lists the target's
/// current settings and compares. That is what turns a deploy-time snapshot into a standing check.
/// </para>
/// </remarks>
public sealed class ConfigurationCheck : IProjectCheckContributor
{
    private readonly DeployAIDbContext _db;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IProviderCredentialTokenService _tokens;

    public ConfigurationCheck(
        DeployAIDbContext db,
        IProviderManagementFactory managementFactory,
        IProviderCredentialTokenService tokens)
    {
        _db = db;
        _managementFactory = managementFactory;
        _tokens = tokens;
    }

    public string Name => "configuration";

    public async Task<IReadOnlyList<ProjectVerificationCheck>> ContributeAsync(
        ProjectCheckContext context,
        CancellationToken cancellationToken)
    {
        var targetIds = context.DeployTargets.Select(t => t.Id).ToList();
        var manifests = await _db.TargetConfigManifests
            .AsNoTracking()
            .Where(m => targetIds.Contains(m.DeployTargetId))
            .ToDictionaryAsync(m => m.DeployTargetId, cancellationToken);

        var checks = new List<ProjectVerificationCheck>();

        foreach (var target in context.DeployTargets)
        {
            var config = DeployTargetConfig.Parse(target.ConfigJson);
            if (!config.IsDeployableTarget)
            {
                continue;
            }

            checks.AddRange(await CheckTargetAsync(
                target, manifests.GetValueOrDefault(target.Id), cancellationToken));
        }

        return checks;
    }

    private async Task<IReadOnlyList<ProjectVerificationCheck>> CheckTargetAsync(
        DeployTarget target,
        TargetConfigManifest? manifest,
        CancellationToken cancellationToken)
    {
        var requiredId = $"config.required:{target.Id}";
        var driftId = $"config.drift:{target.Id}";
        var requiredLabel = "Settings this app needs";
        var driftLabel = "Settings changed since the last publish";

        if (manifest is null)
        {
            // Nothing has been captured for this target yet, which happens until it next deploys.
            // Skipped rather than inconclusive: nothing is wrong and nothing is being retried.
            return
            [
                Check(requiredId, requiredLabel, VerificationCheckStatus.Skipped, target,
                    "DeployAI has not recorded what this app's code needs yet. It will after the next publish.")
            ];
        }

        if (manifest.WasInconclusive)
        {
            // The manifest was captured from a scan that could not read the repository, and it keeps
            // saying so. Treating it as "nothing required" would turn one blind read into a
            // permanent all-clear.
            return
            [
                Check(requiredId, requiredLabel, VerificationCheckStatus.Inconclusive, target,
                    $"DeployAI could not work out what this app's code needs ({manifest.InconclusiveReason ?? "no reason recorded"}).")
            ];
        }

        var required = manifest.RequiredKeys();
        if (required.Count == 0)
        {
            return
            [
                Check(requiredId, requiredLabel, VerificationCheckStatus.Passed, target,
                    "This app's code declares no settings it needs to be given.")
            ];
        }

        IReadOnlyList<ProviderEnvVar> present;
        try
        {
            var management = _managementFactory.GetManagement(target.ProviderName)
                ?? throw new InvalidOperationException($"No management for {target.ProviderName}.");
            var token = await _tokens.GetTokenAsync(target.Credential, cancellationToken);
            present = await management.ListEnvVarsAsync(
                new ProviderCredentials(token), target.ProviderProjectId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return
            [
                Check(requiredId, requiredLabel, VerificationCheckStatus.Inconclusive, target,
                    $"DeployAI could not read this app's settings from {target.ProviderName} ({ex.GetType().Name}).")
            ];
        }

        var checks = new List<ProjectVerificationCheck> { Missing(requiredId, requiredLabel, target, required, present) };

        var drift = Drift(driftId, driftLabel, target, manifest, present);
        if (drift is not null)
        {
            checks.Add(drift);
        }

        return checks;
    }

    private static ProjectVerificationCheck Missing(
        string checkId,
        string label,
        DeployTarget target,
        IReadOnlyList<string> required,
        IReadOnlyList<ProviderEnvVar> present)
    {
        var have = present.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = required
            .Where(key => !have.Contains(key))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            return Check(checkId, label, VerificationCheckStatus.Passed, target,
                $"All {required.Count} setting(s) this app's code needs are set.");
        }

        var noun = missing.Count == 1 ? "setting" : "settings";
        return Check(checkId, label, VerificationCheckStatus.Failed, target,
            $"This app's code needs {missing.Count} {noun} it does not have: {string.Join(", ", missing)}. "
            + "Add them on the app's settings screen.");
    }

    /// <summary>
    /// Whether any required setting's value has changed since the last publish recorded it.
    /// </summary>
    /// <remarks>
    /// A warning, never a failure. Someone editing a value in the provider's UI is a legitimate thing
    /// to do — the point is that it happened outside DeployAI and nothing else would ever say so.
    /// Reporting it as broken would be wrong about a change that may well be the fix.
    /// </remarks>
    private static ProjectVerificationCheck? Drift(
        string checkId,
        string label,
        DeployTarget target,
        TargetConfigManifest manifest,
        IReadOnlyList<ProviderEnvVar> present)
    {
        var fingerprints = manifest.ValueFingerprints();
        if (fingerprints.Count == 0)
        {
            // Nothing readable was recorded to compare against — most often because the provider
            // hides secret values. Silent rather than inconclusive: there is no comparison to make,
            // and a permanent "couldn't check" row would be noise on every sweep forever.
            return null;
        }

        var changed = new List<string>();
        foreach (var variable in present)
        {
            if (!fingerprints.TryGetValue(variable.Key, out var recorded) ||
                variable.ValueHidden ||
                string.IsNullOrEmpty(variable.Value))
            {
                continue;
            }

            if (ConfigValueFingerprint.Compute(target.Id, variable.Key, variable.Value) != recorded)
            {
                changed.Add(variable.Key);
            }
        }

        if (changed.Count == 0)
        {
            return Check(checkId, label, VerificationCheckStatus.Passed, target,
                "No settings have changed since this app was last published.");
        }

        changed.Sort(StringComparer.OrdinalIgnoreCase);
        var noun = changed.Count == 1 ? "setting has" : "settings have";
        return Check(checkId, label, VerificationCheckStatus.Warning, target,
            $"{changed.Count} {noun} changed on {target.ProviderName} since the last publish: "
            + $"{string.Join(", ", changed)}.");
    }

    private static ProjectVerificationCheck Check(
        string checkId,
        string label,
        VerificationCheckStatus status,
        DeployTarget target,
        string message) =>
        new(checkId, VerificationCheckTargets.Configuration, label, status, message, null, null, target.Id);
}
