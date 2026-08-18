using System.Collections.Concurrent;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services.Checks;

/// <summary>
/// Remembers a credential's validity briefly, so one sweep asks each provider once.
/// </summary>
/// <remarks>
/// Singleton, because the sweep's scopes are per project and the saving only exists across them: a
/// user with forty projects on one Coolify instance would otherwise send forty identical
/// authentication checks within the same minute.
/// </remarks>
public interface IProviderCredentialValidationCache
{
    Task<bool?> GetOrValidateAsync(
        Guid credentialId,
        Func<CancellationToken, Task<bool?>> validate,
        CancellationToken cancellationToken);
}

public sealed class ProviderCredentialValidationCache : IProviderCredentialValidationCache
{
    /// <summary>Comfortably longer than one sweep, comfortably shorter than the hour between sweeps.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, (bool? Result, DateTimeOffset At)> _entries = new();

    public async Task<bool?> GetOrValidateAsync(
        Guid credentialId,
        Func<CancellationToken, Task<bool?>> validate,
        CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(credentialId, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < Ttl)
        {
            return cached.Result;
        }

        var result = await validate(cancellationToken);
        _entries[credentialId] = (result, DateTimeOffset.UtcNow);
        return result;
    }
}

/// <summary>
/// Checks that the connections a project's targets depend on still work.
/// </summary>
/// <remarks>
/// <para>
/// Earns its place by naming a cause. When a Coolify token is revoked, every other check on every
/// affected project turns inconclusive at once, and the fleet view fills with identical "could not
/// look" rows that say nothing about why. One credential check per sweep turns that into a single
/// actionable line.
/// </para>
/// <para>
/// It also refreshes <see cref="ProviderCredential.IsValid"/> and
/// <see cref="ProviderCredential.LastValidatedAt"/>, which exist on the entity and were previously
/// written once at connect time and never again — so "valid" meant "was valid when you added it".
/// The write rides on the recorder's save, so a project's credential status and its run land
/// together or not at all.
/// </para>
/// </remarks>
public sealed class ProviderConnectionCheck : IProjectCheckContributor
{
    private readonly DeployAIDbContext _db;
    private readonly IProviderFactory _providerFactory;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IProviderCredentialValidationCache _cache;

    public ProviderConnectionCheck(
        DeployAIDbContext db,
        IProviderFactory providerFactory,
        IProviderCredentialTokenService tokens,
        IProviderCredentialValidationCache cache)
    {
        _db = db;
        _providerFactory = providerFactory;
        _tokens = tokens;
        _cache = cache;
    }

    public string Name => "connections";

    public async Task<IReadOnlyList<ProjectVerificationCheck>> ContributeAsync(
        ProjectCheckContext context,
        CancellationToken cancellationToken)
    {
        var checks = new List<ProjectVerificationCheck>();

        // One check per credential, not per target: two targets on the same Coolify connection share
        // one answer, and reporting it twice would double-count a single problem.
        //
        // Deployable targets only. Object storage and DNS credentials are deliberately not deployment
        // providers — the whole point of CredentialKind is that they never appear in a deploy-target
        // picker — so asking the deployment factory about one yields "no such provider", which this
        // check would then report as an unreachable connection. Found on the first live sweep: a
        // Hetzner storage credential produced a permanent "could not reach hetzner-storage" row that
        // no action would ever clear.
        var credentials = context.DeployTargets
            .Where(t => t.Credential is not null)
            .Where(t => DeployTargetConfig.Parse(t.ConfigJson).IsDeployableTarget)
            .GroupBy(t => t.CredentialId)
            .Select(g => g.First());

        foreach (var target in credentials)
        {
            checks.Add(await CheckCredentialAsync(target, cancellationToken));
        }

        return checks;
    }

    private async Task<ProjectVerificationCheck> CheckCredentialAsync(
        DeployTarget target,
        CancellationToken cancellationToken)
    {
        var checkId = $"provider.connection:{target.CredentialId}";
        var label = $"{target.Credential.Label} connection to {target.ProviderName}";

        var valid = await _cache.GetOrValidateAsync(
            target.CredentialId,
            async ct =>
            {
                try
                {
                    var provider = _providerFactory.GetProvider(target.ProviderName);
                    var token = await _tokens.GetTokenAsync(target.Credential, ct);
                    return await provider.ValidateCredentialsAsync(new ProviderCredentials(token), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // null is the third answer: the provider was not reachable, so the credential is
                    // neither proven good nor proven bad. Returning false here would tell a user to
                    // reconnect a connection that was never broken.
                    return null;
                }
            },
            cancellationToken);

        await RecordValidationAsync(target.CredentialId, valid, cancellationToken);

        return valid switch
        {
            true => Check(checkId, label, VerificationCheckStatus.Passed, target,
                $"The {target.ProviderName} connection works."),
            false => Check(checkId, label, VerificationCheckStatus.Failed, target,
                $"{target.ProviderName} rejected this connection. Reconnect it in settings.", "reconnect"),
            _ => Check(checkId, label, VerificationCheckStatus.Inconclusive, target,
                $"DeployAI could not reach {target.ProviderName} to check this connection.")
        };
    }

    /// <summary>
    /// Updates the credential's recorded validity — but only on a conclusive answer, so an
    /// unreachable provider never marks a working connection invalid.
    /// </summary>
    private async Task RecordValidationAsync(
        Guid credentialId,
        bool? valid,
        CancellationToken cancellationToken)
    {
        if (valid is not { } conclusive)
        {
            return;
        }

        var credential = await _db.ProviderCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId, cancellationToken);
        if (credential is null)
        {
            return;
        }

        credential.IsValid = conclusive;
        credential.LastValidatedAt = DateTimeOffset.UtcNow;
    }

    private static ProjectVerificationCheck Check(
        string checkId,
        string label,
        VerificationCheckStatus status,
        DeployTarget target,
        string message,
        string? suggestedAction = null) =>
        new(checkId, VerificationCheckTargets.Provider, label, status, message, null, suggestedAction, target.Id);
}
