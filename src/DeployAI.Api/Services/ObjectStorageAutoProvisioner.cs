using DeployAI.Core.Deployments;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>
/// What the deploy should say about storage. Every case reports something: an app that needs a
/// bucket and got one, and an app that needs one and could not get one, are both worth a line in
/// the log. Only an app with no storage need at all stays quiet.
/// </summary>
public sealed record ObjectStorageAutoOutcome(bool Needed, string? Message);

public interface IObjectStorageAutoProvisioner
{
    Task<ObjectStorageAutoOutcome> EnsureAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provisions a bucket during a deploy for an app that stores files, without anyone having to know
/// they needed one.
/// </summary>
/// <remarks>
/// Object storage used to be something a user had to recognise they needed and request by name,
/// which a non-technical user cannot do. The failure it prevents is quiet: an app whose storage is
/// unconfigured falls back to the container filesystem, accepts the upload, returns success, and
/// loses the file on the next redeploy. Nothing errors, and the user finds out when a photo they
/// uploaded last week is gone.
/// <para>
/// Reads the repository rather than asking. When the evidence says the app talks to object storage
/// and no bucket is linked yet, it provisions one and reports which files said so.
/// </para>
/// </remarks>
public sealed class ObjectStorageAutoProvisioner : IObjectStorageAutoProvisioner
{
    private readonly DeployAIDbContext _db;
    private readonly IGitHubService _gitHub;
    private readonly IEncryptionService _encryption;
    private readonly IObjectStorageNeedDetector _detector;
    private readonly IObjectStorageProvisioningService _provisioning;
    private readonly ILogger<ObjectStorageAutoProvisioner> _logger;

    public ObjectStorageAutoProvisioner(
        DeployAIDbContext db,
        IGitHubService gitHub,
        IEncryptionService encryption,
        IObjectStorageNeedDetector detector,
        IObjectStorageProvisioningService provisioning,
        ILogger<ObjectStorageAutoProvisioner> logger)
    {
        _db = db;
        _gitHub = gitHub;
        _encryption = encryption;
        _detector = detector;
        _provisioning = provisioning;
        _logger = logger;
    }

    public async Task<ObjectStorageAutoOutcome> EnsureAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken)
    {
        var parts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return new ObjectStorageAutoOutcome(false, null);
        }

        // Already has a bucket: nothing to decide, and re-reading the repo would only risk
        // re-provisioning something that is already wired.
        var alreadyLinked = project.DeployTargets
            .Any(t => DeployTargetConfig.Parse(t.ConfigJson).IsStorageTarget);
        if (alreadyLinked)
        {
            return new ObjectStorageAutoOutcome(true, null);
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var token = _encryption.Decrypt(user.GitHubTokenEncrypted);
        var config = DeployTargetConfig.Parse(serverTarget.ConfigJson);
        var serviceDir = Normalize(config.ServiceDirectory ?? config.RootDirectory);

        var need = _detector.Detect(new ObjectStorageScanInputs(
            AppsettingsContent: await ReadAsync(token, parts[0], parts[1], Join(serviceDir, "appsettings.json"), branch, cancellationToken),
            ComposeContent: await ReadAsync(token, parts[0], parts[1], "docker-compose.yml", branch, cancellationToken),
            DotEnvExampleContent: await ReadAsync(token, parts[0], parts[1], ".env.example", branch, cancellationToken),
            ManifestContents: await ReadManifestsAsync(token, parts[0], parts[1], serviceDir, branch, cancellationToken)));

        if (!need.Needed)
        {
            return new ObjectStorageAutoOutcome(false, null);
        }

        var reasons = string.Join("; ", need.Evidence);

        // A storage connection is the one thing DeployAI cannot invent: the keys come from the
        // user's own provider account. Saying so is the whole point -- silence here is what leaves
        // an app writing uploads to a filesystem that will not survive its next deploy.
        var hasConnection = await _db.ProviderCredentials
            .AnyAsync(c => c.UserId == project.UserId && c.Kind == CredentialKind.ObjectStorage, cancellationToken);

        if (!hasConnection)
        {
            return new ObjectStorageAutoOutcome(true,
                $"This app stores files ({reasons}), but no object storage is connected. " +
                "Uploads will be written inside the container and lost on the next deploy. " +
                "Connect object storage in settings to fix it.");
        }

        try
        {
            var result = await _provisioning.ProvisionAsync(project.UserId, project.Id, cancellationToken);
            return result is null
                ? new ObjectStorageAutoOutcome(true, $"This app stores files ({reasons}), but a bucket could not be provisioned.")
                : new ObjectStorageAutoOutcome(true,
                    $"Provisioned object storage '{result.Bucket}' and set {string.Join(", ", result.AppliedKeys)} — {reasons}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Advisory: a bucket that could not be created must not fail a deploy that would
            // otherwise succeed. It must be reported, though -- see above for why silence is worse.
            _logger.LogWarning(ex, "Could not provision object storage for project {ProjectId}.", project.Id);
            return new ObjectStorageAutoOutcome(true,
                $"This app stores files ({reasons}), but provisioning a bucket failed: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> ReadManifestsAsync(
        string token, string owner, string repo, string serviceDir, string branch, CancellationToken cancellationToken)
    {
        var candidates = new List<string> { "package.json", "requirements.txt" };
        if (!string.IsNullOrEmpty(serviceDir))
        {
            candidates.Add(Join(serviceDir, "package.json"));
            candidates.Add(Join(serviceDir, "requirements.txt"));
        }

        var contents = new List<string>();
        foreach (var path in candidates)
        {
            var content = await ReadAsync(token, owner, repo, path, branch, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                contents.Add(content);
            }
        }

        // The csproj name is not knowable up front, so the service directory is listed for one.
        if (!string.IsNullOrEmpty(serviceDir))
        {
            try
            {
                var items = await _gitHub.ListAllContentsAsync(token, owner, repo, serviceDir, branch, cancellationToken);
                foreach (var csproj in items.Where(i => i.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
                {
                    var content = await ReadAsync(token, owner, repo, csproj.Path, branch, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        contents.Add(content);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not list {Directory} while scanning for storage use.", serviceDir);
            }
        }

        return contents;
    }

    private async Task<string?> ReadAsync(
        string token, string owner, string repo, string path, string branch, CancellationToken cancellationToken)
    {
        try
        {
            return await _gitHub.GetFileContentAsync(token, owner, repo, path, branch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read {Path} while scanning for storage use.", path);
            return null;
        }
    }

    private static string Normalize(string? path) =>
        path?.Trim().Replace('\\', '/').Trim('/') ?? string.Empty;

    private static string Join(string directory, string file) =>
        string.IsNullOrEmpty(directory) ? file : $"{directory}/{file}";
}
