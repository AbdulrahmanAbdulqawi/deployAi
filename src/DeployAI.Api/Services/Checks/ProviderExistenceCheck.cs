using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data.Entities;

namespace DeployAI.Api.Services.Checks;

/// <summary>
/// Asks each provider whether the application DeployAI deployed is actually still there.
/// </summary>
/// <remarks>
/// <para>
/// The check that closes the oldest hole in DeployAI's dashboard: a project whose Coolify
/// applications had been deleted still showed as deployed and healthy, with links to domains that
/// returned 404, because nothing ever asked the provider again after the deploy succeeded. Status
/// was recorded once and believed forever.
/// </para>
/// <para>
/// Runs every sweep against every deployable target, including the ones that were fine last time.
/// "Already deployed" is precisely the state that stops being true without anyone doing anything.
/// </para>
/// </remarks>
public sealed class ProviderExistenceCheck : IProjectCheckContributor
{
    private readonly IProviderApplicationExistenceFactory _existenceFactory;
    private readonly IProviderCredentialTokenService _tokens;

    public ProviderExistenceCheck(
        IProviderApplicationExistenceFactory existenceFactory,
        IProviderCredentialTokenService tokens)
    {
        _existenceFactory = existenceFactory;
        _tokens = tokens;
    }

    public string Name => "provider";

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
                // A database or a bucket is not an application, and asking the applications endpoint
                // about one would produce a confident 404 meaning nothing.
                continue;
            }

            checks.Add(await CheckTargetAsync(target, config, cancellationToken));
        }

        return checks;
    }

    private async Task<ProjectVerificationCheck> CheckTargetAsync(
        DeployTarget target,
        DeployTargetConfig config,
        CancellationToken cancellationToken)
    {
        var checkId = $"provider.application_exists:{target.Id}";
        var label = $"{Describe(config)} exists on {target.ProviderName}";

        var existence = _existenceFactory.GetApplicationExistence(target.ProviderName);
        if (existence is null)
        {
            // Skipped, not inconclusive: this provider offers no way to ask, so there is nothing
            // here that a retry or a fix would ever change.
            return Check(checkId, label, VerificationCheckStatus.Skipped, target,
                $"{target.ProviderName} does not expose a way to check whether an app still exists.");
        }

        ProviderApplicationExistence result;
        try
        {
            var token = await _tokens.GetTokenAsync(target.Credential, cancellationToken);
            result = await existence.CheckApplicationExistsAsync(
                new ProviderCredentials(token), target.ProviderProjectId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Check(checkId, label, VerificationCheckStatus.Inconclusive, target,
                $"DeployAI could not check this app with {target.ProviderName} ({ex.GetType().Name}).");
        }

        return result.Presence switch
        {
            ProviderApplicationPresence.Absent =>
                Check(checkId, label, VerificationCheckStatus.Failed, target, result.Detail, result.DeployUrl),
            ProviderApplicationPresence.Unknown =>
                Check(checkId, label, VerificationCheckStatus.Inconclusive, target, result.Detail),
            _ => FromRunningState(checkId, label, target, result)
        };
    }

    /// <summary>
    /// Turns the provider's own word for what the application is doing into a verdict.
    /// </summary>
    /// <remarks>
    /// A container that exists but is stopped is a failure, not a pass: Coolify stops an application
    /// entirely once it has crash-looped past its restart limit, so "present but exited" is the exact
    /// shape of an app that died and stayed dead. A deploy in progress is a warning rather than a
    /// failure, because it is expected to resolve on its own.
    /// </remarks>
    private static ProjectVerificationCheck FromRunningState(
        string checkId,
        string label,
        DeployTarget target,
        ProviderApplicationExistence result)
    {
        var state = result.State?.ToLowerInvariant() ?? string.Empty;

        if (state.Contains("exited", StringComparison.Ordinal) ||
            state.Contains("stopped", StringComparison.Ordinal) ||
            state.Contains("failed", StringComparison.Ordinal) ||
            state.Contains("crashed", StringComparison.Ordinal))
        {
            return Check(checkId, label, VerificationCheckStatus.Failed, target,
                $"The app exists on {target.ProviderName} but is not running (\"{result.State}\").",
                result.DeployUrl, "redeploy_server");
        }

        if (state.Contains("deploying", StringComparison.Ordinal) ||
            state.Contains("building", StringComparison.Ordinal) ||
            state.Contains("queued", StringComparison.Ordinal) ||
            state.Contains("starting", StringComparison.Ordinal))
        {
            return Check(checkId, label, VerificationCheckStatus.Warning, target,
                $"The app is mid-deploy on {target.ProviderName} (\"{result.State}\").", result.DeployUrl);
        }

        return Check(checkId, label, VerificationCheckStatus.Passed, target, result.Detail, result.DeployUrl);
    }

    private static string Describe(DeployTargetConfig config) => config.Role switch
    {
        DeploymentPartRoles.Website => "Website",
        DeploymentPartRoles.Server => "API",
        _ => "App"
    };

    private static ProjectVerificationCheck Check(
        string checkId,
        string label,
        VerificationCheckStatus status,
        DeployTarget target,
        string message,
        string? url = null,
        string? suggestedAction = null) =>
        new(checkId, VerificationCheckTargets.Provider, label, status, message, url, suggestedAction, target.Id);
}
