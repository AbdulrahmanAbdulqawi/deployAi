using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

public sealed record ObjectStorageProvisioningResult(
    string Bucket,
    IReadOnlyList<string> AppliedKeys);

public interface IObjectStorageProvisioningService
{
    /// <summary>
    /// Ensures the bucket exists and writes its connection onto the app's server target.
    /// Returns null when the project has no storage part — having no bucket is normal.
    /// </summary>
    Task<ObjectStorageProvisioningResult?> ProvisionAsync(
        Guid userId,
        Guid projectId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The bridge between the storage capability and deployments.
///
/// <see cref="IObjectStorageProvider"/>'s own doc comment says storage providers must never
/// appear in deploy-target pickers, and that stays true — a bucket is still not somewhere an app
/// is deployed. But an app needs one provisioned and its keys wired in, and this is the only
/// place the two worlds meet.
/// </summary>
public sealed class ObjectStorageProvisioningService : IObjectStorageProvisioningService
{
    private readonly DeployAIDbContext _db;
    private readonly IObjectStorageProviderFactory _storageFactory;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IEncryptionService _encryption;

    public ObjectStorageProvisioningService(
        DeployAIDbContext db,
        IObjectStorageProviderFactory storageFactory,
        IProviderManagementFactory managementFactory,
        IEncryptionService encryption)
    {
        _db = db;
        _storageFactory = storageFactory;
        _managementFactory = managementFactory;
        _encryption = encryption;
    }

    public async Task<ObjectStorageProvisioningResult?> ProvisionAsync(
        Guid userId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);

        if (project is null)
        {
            throw new DeployAIException("not_found", "We couldn't find that app.");
        }

        var targets = project.DeployTargets
            .Select(target => (Target: target, Config: DeployTargetConfig.Parse(target.ConfigJson)))
            .ToList();

        var storage = targets.FirstOrDefault(t => t.Config.IsStorageTarget);

        // Keyed off role, not provider name: under a single-origin compose plan both halves
        // share the Coolify provider, so matching on provider would pick the wrong target.
        var server = targets.FirstOrDefault(t =>
            DeploymentPartRoles.Is(t.Config.Role, DeploymentPartRoles.Server));

        if (server.Target is null)
        {
            throw new DeployAIException(
                "storage_no_server",
                "This app has no server to wire a bucket into.");
        }

        var connectionCredential = await ResolveStorageCredentialAsync(
            userId,
            storage.Target is null ? new DeployTargetConfig() : storage.Config,
            cancellationToken);

        if (storage.Target is null)
        {
            // Nothing else in the codebase ever created one. DeploymentPartRoles.Storage was
            // referenced in exactly one place -- the predicate testing for it -- so this branch
            // returned null for every project, and the whole object-storage capability (provider,
            // provisioning, env wiring, twelve endpoints) could not be reached at all.
            //
            // Creating it here is safe because this runs only from an explicit request to
            // provision storage for one app; nothing in the deploy pipeline calls it, so no app
            // gets a bucket it did not ask for.
            var created = new DeployTarget
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                ProviderName = connectionCredential.ProviderName,
                CredentialId = connectionCredential.Id,
                ConfigJson = new DeployTargetConfig
                {
                    Role = DeploymentPartRoles.Storage,
                    // Which connection the bucket belongs to, so a second connection added later
                    // cannot silently re-point an app at a different account's storage.
                    RailwayProjectId = connectionCredential.Id.ToString()
                }.ToJson(),
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.DeployTargets.Add(created);
            storage = (created, DeployTargetConfig.Parse(created.ConfigJson));
        }
        var provider = _storageFactory.GetObjectStorage(connectionCredential.ProviderName)
            ?? throw new DeployAIException(
                "storage_provider_unavailable",
                $"No storage provider is registered for {connectionCredential.ProviderName}.");

        var storageCredentials = new ProviderCredentials(_encryption.Decrypt(connectionCredential.TokenEncrypted));
        var payload = StorageCredentialStorage.TryParse(storageCredentials.Token)
            ?? throw new DeployAIException(
                "storage_credentials_invalid",
                "That storage connection is missing its endpoint or keys. Reconnect it in settings.");

        var bucketName = string.IsNullOrWhiteSpace(storage.Config.LinkedServiceName)
            ? BuildBucketName(project.Name)
            : storage.Config.LinkedServiceName;

        var bucket = await provider.CreateBucketAsync(storageCredentials, bucketName, cancellationToken);

        // The browser PUTs to the bucket directly using a presigned URL, so the API's CORS policy
        // is not in that request's path -- the bucket's own is. A new bucket allows no origin, so
        // the preflight returns 403 and the upload never starts, from an app that is otherwise
        // correctly wired. Told here, on every deploy, because a site's origin changes with its
        // domain.
        var websiteOrigins = await ResolveWebsiteOriginsAsync(project.Id, cancellationToken);
        if (websiteOrigins.Count > 0)
        {
            await provider.SetBucketCorsAsync(storageCredentials, bucket.Name, websiteOrigins, cancellationToken);
        }

        var connection = new ObjectStorageConnection(
            payload.Endpoint,
            payload.Region,
            bucket.Name,
            payload.AccessKey,
            payload.SecretKey);

        var applied = await ApplyEnvVarsAsync(userId, server.Target, server.Config, connection, cancellationToken);

        // Remember which bucket this app owns, so the next deploy reuses it instead of
        // minting a second one from a renamed project.
        storage.Config.LinkedServiceName = bucket.Name;
        storage.Target.ConfigJson = storage.Config.ToJson();
        await _db.SaveChangesAsync(cancellationToken);

        return new ObjectStorageProvisioningResult(bucket.Name, applied);
    }

    /// <summary>
    /// The origins a browser will upload from: the website's last known deployed URL. Taken from
    /// the deployment record rather than guessed, because that is the address the browser actually
    /// sends as <c>Origin</c>, and a rule for any other value silently allows nothing.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveWebsiteOriginsAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var urls = await _db.DeploymentTargets
            .Where(dt => dt.Deployment.ProjectId == projectId
                         && dt.DeployUrl != null
                         && dt.Status == DeploymentStatuses.Success)
            .OrderByDescending(dt => dt.CompletedAt)
            .Select(dt => new { dt.DeployUrl, dt.DeployTargetId })
            .Take(20)
            .ToListAsync(cancellationToken);

        var websiteTargetIds = await _db.DeployTargets
            .Where(t => t.ProjectId == projectId)
            .Select(t => new { t.Id, t.ConfigJson })
            .ToListAsync(cancellationToken);

        var websiteIds = websiteTargetIds
            .Where(t => DeploymentPartRoles.Is(DeployTargetConfig.Parse(t.ConfigJson).Role, DeploymentPartRoles.Website))
            .Select(t => t.Id)
            .ToHashSet();

        return urls
            .Where(u => websiteIds.Contains(u.DeployTargetId))
            .Select(u => u.DeployUrl!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> ApplyEnvVarsAsync(
        Guid userId,
        DeployTarget serverTarget,
        DeployTargetConfig serverConfig,
        ObjectStorageConnection connection,
        CancellationToken cancellationToken)
    {
        var management = _managementFactory.GetManagement(serverTarget.ProviderName)
            ?? throw new DeployAIException(
                "unsupported_provider",
                $"{serverTarget.ProviderName} can't have environment variables set from here.");

        var credential = await _db.ProviderCredentials
            .FirstOrDefaultAsync(
                c => c.UserId == userId &&
                     c.ProviderName == serverTarget.ProviderName &&
                     c.Kind == CredentialKind.Deployment,
                cancellationToken)
            ?? throw new DeployAIException(
                "credential_missing",
                $"Connect {serverTarget.ProviderName} before provisioning storage.");

        var providerCredentials = new ProviderCredentials(_encryption.Decrypt(credential.TokenEncrypted));
        var assignments = ObjectStorageEnvironmentWiring.BuildAssignments(serverConfig.Framework, connection);
        var applied = new List<string>();

        foreach (var assignment in assignments)
        {
            await management.UpsertEnvVarAsync(
                providerCredentials,
                serverTarget.ProviderProjectId!,
                new UpsertProviderEnvVarRequest(
                    assignment.Key,
                    assignment.Value,
                    // Access keys must not be readable back out of the platform's UI.
                    assignment.IsSecret ? ProviderEnvVarTypes.Secret : ProviderEnvVarTypes.Plain,
                    []),
                cancellationToken);

            applied.Add(assignment.Key);
        }

        return applied;
    }

    private async Task<ProviderCredential> ResolveStorageCredentialAsync(
        Guid userId,
        DeployTargetConfig storageConfig,
        CancellationToken cancellationToken)
    {
        var query = _db.ProviderCredentials
            .Where(c => c.UserId == userId && c.Kind == CredentialKind.ObjectStorage);

        if (!string.IsNullOrWhiteSpace(storageConfig.RailwayProjectId) &&
            Guid.TryParse(storageConfig.RailwayProjectId, out var credentialId))
        {
            query = query.Where(c => c.Id == credentialId);
        }

        var credentials = await query.ToListAsync(cancellationToken);

        return credentials.Count switch
        {
            0 => throw new DeployAIException(
                "storage_connection_missing",
                "Connect object storage in settings before provisioning a bucket."),
            1 => credentials[0],
            _ => throw new DeployAIException(
                "storage_connection_ambiguous",
                "You have more than one storage connection. Pick which one this app should use: " +
                string.Join(", ", credentials.Select(c => c.Label)))
        };
    }

    /// <summary>
    /// Bucket names are globally unique per endpoint and constrained to lowercase DNS-ish
    /// characters, so a project name can't be used as-is.
    /// </summary>
    internal static string BuildBucketName(string projectName)
    {
        var slug = new string(projectName
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length < 3)
        {
            slug = $"app-{slug}".Trim('-');
        }

        return slug.Length > 63 ? slug[..63].Trim('-') : slug;
    }
}
