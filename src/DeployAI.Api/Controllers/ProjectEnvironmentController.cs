using System.Text;
using System.Text.Json;
using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/environment")]
public sealed class ProjectEnvironmentController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IFrontendEnvironmentWiringService _frontendEnvironmentWiring;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IEncryptionService _encryption;

    public ProjectEnvironmentController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IFrontendEnvironmentWiringService frontendEnvironmentWiring,
        IProviderManagementFactory managementFactory,
        IEncryptionService encryption)
    {
        _db = db;
        _currentUser = currentUser;
        _frontendEnvironmentWiring = frontendEnvironmentWiring;
        _managementFactory = managementFactory;
        _encryption = encryption;
    }

    /// <summary>
    /// Persists the project's env vars (encrypted) and pushes them onto its Coolify app,
    /// before the first deploy — the sequence that lets the app boot configured instead of
    /// crash-looping on missing secrets. Returns key names only, never values.
    /// </summary>
    [HttpPost("compose")]
    public async Task<IActionResult> SetComposeEnvironment(
        Guid projectId,
        [FromBody] SetComposeEnvironmentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Variables.Count == 0)
        {
            return Ok(new { applied = Array.Empty<string>() });
        }

        var userId = _currentUser.UserId
            ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken)
            ?? throw new DeployAIException("not_found", "We couldn't find that app.");

        // Role-keyed, not provider-keyed: under a compose plan every target shares "coolify".
        var target = project.DeployTargets
            .Select(t => (Target: t, Config: DeployTargetConfig.Parse(t.ConfigJson)))
            .Where(t => t.Config.IsDeployableTarget && !string.IsNullOrWhiteSpace(t.Target.ProviderProjectId))
            .OrderByDescending(t => DeploymentPartRoles.Is(t.Config.Role, DeploymentPartRoles.Website))
            .Select(t => t.Target)
            .FirstOrDefault()
            ?? throw new DeployAIException("no_target", "This app has no deploy target to receive environment variables.");

        var management = _managementFactory.GetManagement(target.ProviderName);
        var credential = await _db.ProviderCredentials
            .FirstOrDefaultAsync(
                c => c.UserId == userId &&
                     c.ProviderName == target.ProviderName &&
                     c.Kind == CredentialKind.Deployment,
                cancellationToken)
            ?? throw new DeployAIException("credential_missing", $"Connect {target.ProviderName} first.");

        var providerCredentials = new ProviderCredentials(_encryption.Decrypt(credential.TokenEncrypted));
        var applied = new List<string>();

        foreach (var variable in request.Variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Key) || string.IsNullOrEmpty(variable.Value))
            {
                continue;
            }

            await management.UpsertEnvVarAsync(
                providerCredentials,
                target.ProviderProjectId!,
                new UpsertProviderEnvVarRequest(
                    variable.Key.Trim(),
                    variable.Value,
                    variable.IsSecret ? ProviderEnvVarTypes.Secret : ProviderEnvVarTypes.Plain,
                    []),
                cancellationToken);

            applied.Add(variable.Key.Trim());
        }

        // Persisted after the push succeeds, so the stored copy never claims more than the
        // provider actually has. Merged over what's already stored — pushing one corrected
        // var must not erase the record of the other thirteen. Same encryption discipline
        // as provider credentials.
        var stored = new Dictionary<string, StoredEnvVar>(StringComparer.OrdinalIgnoreCase);
        if (project.EnvironmentVariablesEncrypted is { Length: > 0 })
        {
            try
            {
                var existing = JsonSerializer.Deserialize<Dictionary<string, StoredEnvVar>>(
                    _encryption.Decrypt(project.EnvironmentVariablesEncrypted));
                foreach (var (key, value) in existing ?? [])
                {
                    stored[key] = value;
                }
            }
            catch (JsonException)
            {
                // Unreadable old blob — fall through and store what we just pushed.
            }
        }

        foreach (var variable in request.Variables
                     .Where(v => !string.IsNullOrWhiteSpace(v.Key) && !string.IsNullOrEmpty(v.Value)))
        {
            stored[variable.Key.Trim()] = new StoredEnvVar(variable.Value, variable.IsSecret);
        }

        project.EnvironmentVariablesEncrypted = _encryption.Encrypt(JsonSerializer.Serialize(stored));
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { applied });
    }

    public sealed record SetComposeEnvironmentRequest(IReadOnlyList<IncomingEnvVar> Variables);

    public sealed record IncomingEnvVar(string Key, string Value, bool IsSecret);

    private sealed record StoredEnvVar(string Value, bool IsSecret);

    [HttpGet("sync")]
    public async Task<IActionResult> GetSyncStatus(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await GetOwnedProjectAsync(projectId, cancellationToken);
        var state = ProjectEnvironmentSyncState.Parse(project.EnvironmentSyncJson);
        return Ok(MapSyncState(state));
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(
        Guid projectId,
        [FromQuery] bool redeployRailway = true,
        CancellationToken cancellationToken = default)
    {
        _ = await GetOwnedProjectAsync(projectId, cancellationToken);

        var result = await _frontendEnvironmentWiring.SyncCrossProviderEnvironmentAsync(
            projectId,
            new EnvironmentSyncOptions(
                RedeployRailwayAfterUpdate: redeployRailway,
                RedeployVercelAfterUpdate: false,
                EnsureWebsiteWiring: true,
                ApplyVercelEnv: true,
                ApplyRailwayEnv: true,
                RunVerification: true,
                Source: "manual"),
            cancellationToken);

        return Ok(MapResult(result));
    }

    private async Task<Data.Entities.Project> GetOwnedProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId
            ?? throw new DeployAIException("unauthorized", "Sign in to continue.");

        return await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken)
            ?? throw new DeployAIException("not_found", "We couldn't find that app.");
    }

    private static object MapSyncState(ProjectEnvironmentSyncState? state) =>
        state is null
            ? new { synced = false }
            : new
            {
                synced = true,
                lastSyncedAt = state.LastSyncedAt,
                source = state.Source,
                success = state.Success,
                driftDetected = state.DriftDetected,
                resolvedWebsiteUrl = state.ResolvedWebsiteUrl,
                resolvedApiUrl = state.ResolvedApiUrl,
                verificationMessages = state.VerificationMessages,
                driftDetails = state.DriftDetails
            };

    private static object MapResult(EnvironmentSyncResult result) => new
    {
        success = result.Success,
        skipped = result.Skipped,
        skipReason = result.SkipReason,
        driftDetected = result.DriftDetected,
        resolvedWebsiteUrl = result.ResolvedWebsiteUrl,
        resolvedApiUrl = result.ResolvedApiUrl,
        railwayKeysApplied = result.RailwayKeysApplied,
        vercelKeysApplied = result.VercelKeysApplied,
        verificationMessages = result.VerificationMessages,
        driftDetails = result.DriftDetails,
        source = result.Source,
        completedAt = result.CompletedAt
    };
}
