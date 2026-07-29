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

        // Already linked: the decision is made, so the repository scan is skipped — but the
        // provisioning itself still runs. It is idempotent, and it is what keeps the bucket's CORS
        // rule pointing at the site's current origin and the five keys present on the server.
        //
        // Returning early here instead was a bug of exactly the kind this file exists to prevent:
        // an operation that only runs at creation time. The bucket was created before DeployAI
        // could set CORS on it, and once the link existed nothing ever revisited it, so the rule
        // was never applied and uploads kept failing their preflight. The same short-circuit would
        // have silently broken uploads on any domain change.
        var alreadyLinked = project.DeployTargets
            .Any(t => DeployTargetConfig.Parse(t.ConfigJson).IsStorageTarget);
        if (alreadyLinked)
        {
            try
            {
                var refreshed = await _provisioning.ProvisionAsync(project.UserId, project.Id, cancellationToken);

                // Quiet when an unchanged bucket verifies -- re-affirming it every deploy is not
                // news. Loud the moment it does not, because a bucket that stops working looks
                // exactly like one that works until somebody tries to upload.
                return new ObjectStorageAutoOutcome(true, UnhealthyMessage(refreshed?.Verification));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not refresh object storage for project {ProjectId}.", project.Id);
                return new ObjectStorageAutoOutcome(true,
                    $"This app's object storage could not be refreshed: {ex.Message}");
            }
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var token = _encryption.Decrypt(user.GitHubTokenEncrypted);
        var config = DeployTargetConfig.Parse(serverTarget.ConfigJson);
        var serviceDir = Normalize(config.ServiceDirectory ?? config.RootDirectory);

        // The service directory of a modular solution holds project directories and no files of its
        // own, so reading only that level finds neither appsettings.json nor a csproj. Descending
        // one level is what makes a monorepo visible: without it the scan came back empty for
        // exactly the app this was written for, and empty reads as "stores no files".
        var directories = await ScanDirectoriesAsync(token, parts[0], parts[1], serviceDir, branch, cancellationToken);

        var need = _detector.Detect(new ObjectStorageScanInputs(
            AppsettingsContent: await ReadFirstAsync(token, parts[0], parts[1], directories, "appsettings.json", branch, cancellationToken),
            ComposeContent: await ReadAsync(token, parts[0], parts[1], "docker-compose.yml", branch, cancellationToken),
            DotEnvExampleContent: await ReadAsync(token, parts[0], parts[1], ".env.example", branch, cancellationToken),
            ManifestContents: await ReadManifestsAsync(token, parts[0], parts[1], directories, branch, cancellationToken)));

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
            if (result is null)
            {
                return new ObjectStorageAutoOutcome(true,
                    $"This app stores files ({reasons}), but a bucket could not be provisioned.");
            }

            // The verification findings go in the same line as the provisioning result, so what was
            // proven sits next to what was configured rather than in a separate log nobody reads.
            var proven = result.Verification is { Findings.Count: > 0 }
                ? " " + string.Join(" ", result.Verification.Findings)
                : string.Empty;

            return new ObjectStorageAutoOutcome(true,
                $"Provisioned object storage '{result.Bucket}' and set {string.Join(", ", result.AppliedKeys)} — {reasons}.{proven}");
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

    /// <summary>
    /// The service directory plus its immediate children, which is where a modular solution keeps
    /// the project that actually holds the configuration. One level only: deeper is a repository
    /// crawl, and the cost is paid on every deploy.
    /// </summary>
    private async Task<IReadOnlyList<string>> ScanDirectoriesAsync(
        string token, string owner, string repo, string serviceDir, string branch, CancellationToken cancellationToken)
    {
        var directories = new List<string> { serviceDir };

        try
        {
            var items = await _gitHub.ListAllContentsAsync(token, owner, repo, serviceDir, branch, cancellationToken);
            directories.AddRange(items
                .Where(i => string.Equals(i.Type, "dir", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Path));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not list {Directory} while scanning for storage use.", serviceDir);
        }

        return directories;
    }

    /// <summary>The first directory that has the file — the nearest one wins.</summary>
    private async Task<string?> ReadFirstAsync(
        string token, string owner, string repo, IReadOnlyList<string> directories,
        string fileName, string branch, CancellationToken cancellationToken)
    {
        foreach (var directory in directories)
        {
            var content = await ReadAsync(token, owner, repo, Join(directory, fileName), branch, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }
        }

        return null;
    }

    /// <summary>
    /// The findings of a verification that failed, or null when there is nothing wrong to report.
    /// Keeps the quiet-on-success rule in one place rather than at each call site.
    /// </summary>
    private static string? UnhealthyMessage(ObjectStorageVerification? verification) =>
        verification is { Ok: false, Findings.Count: > 0 }
            ? string.Join(" ", verification.Findings)
            : null;

    private async Task<IReadOnlyList<string>> ReadManifestsAsync(
        string token, string owner, string repo, IReadOnlyList<string> directories,
        string branch, CancellationToken cancellationToken)
    {
        var contents = new List<string>();

        foreach (var directory in directories)
        {
            foreach (var name in (string[])["package.json", "requirements.txt"])
            {
                var content = await ReadAsync(token, owner, repo, Join(directory, name), branch, cancellationToken);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    contents.Add(content);
                }
            }

            // A csproj's name is not knowable up front, so the directory is listed for one.
            try
            {
                var items = await _gitHub.ListAllContentsAsync(token, owner, repo, directory, branch, cancellationToken);
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
                _logger.LogDebug(ex, "Could not list {Directory} while scanning for storage use.", directory);
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
