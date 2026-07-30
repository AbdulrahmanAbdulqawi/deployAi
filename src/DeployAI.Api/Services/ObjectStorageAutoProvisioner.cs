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
    private readonly IRepositoryLayoutResolver _layoutResolver;
    private readonly IRepositoryReader _reader;
    private readonly IEncryptionService _encryption;
    private readonly IObjectStorageNeedDetector _detector;
    private readonly IObjectStorageProvisioningService _provisioning;
    private readonly ILogger<ObjectStorageAutoProvisioner> _logger;

    public ObjectStorageAutoProvisioner(
        DeployAIDbContext db,
        IRepositoryLayoutResolver layoutResolver,
        IRepositoryReader reader,
        IEncryptionService encryption,
        IObjectStorageNeedDetector detector,
        IObjectStorageProvisioningService provisioning,
        ILogger<ObjectStorageAutoProvisioner> logger)
    {
        _db = db;
        _layoutResolver = layoutResolver;
        _reader = reader;
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
                return new ObjectStorageAutoOutcome(true, VerificationMessage(refreshed?.Verification));
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

        // One shared resolver rather than this caller's own idea of where to look. It descends a
        // level when the service directory holds only project directories, which is what makes a
        // monorepo visible -- without it the scan came back empty for exactly the app this was
        // written for, and empty reads as "stores no files".
        var layout = await _layoutResolver.ResolveAsync(
            token, parts[0], parts[1], branch, serviceDir, cancellationToken);

        // A scan that could list nothing is not a scan that found nothing. Saying so beats reporting
        // "this app stores no files" about a repository nobody could see.
        if (layout.IsInconclusive)
        {
            _logger.LogWarning(
                "Could not read {Repo}@{Branch} under '{Directory}', so whether it stores files is unknown.",
                project.GitHubRepoFullName, branch, serviceDir);
            return new ObjectStorageAutoOutcome(false,
                $"Could not read this app's files under '{serviceDir}', so DeployAI cannot tell whether it needs storage.");
        }

        var appsettings = await _reader.FindAsync(
            token, parts[0], parts[1], branch, layout, "appsettings.json", cancellationToken);
        var dotEnv = await _reader.FindAsync(
            token, parts[0], parts[1], branch, layout, ".env.example", cancellationToken);
        var compose = await _reader.FindAsync(
            token, parts[0], parts[1], branch, layout, "docker-compose.yml", cancellationToken);

        var need = _detector.Detect(new ObjectStorageScanInputs(
            AppsettingsContent: appsettings?.Content,
            ComposeContent: compose?.Content,
            DotEnvExampleContent: dotEnv?.Content,
            ManifestContents: await ReadManifestsAsync(token, parts[0], parts[1], branch, layout, cancellationToken)));

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
    /// What to say about a verification — including when it passed, and when it could not run.
    /// </summary>
    /// <remarks>
    /// This reported only failures at first, on the reasoning that re-affirming an unchanged bucket
    /// every deploy is not news. That was wrong in the specific way this whole file exists to avoid:
    /// it made "verified clean" and "never checked" produce byte-identical output, so the only way to
    /// tell them apart was to read DeployAI's own HTTP client logs. Nobody does that. A passing
    /// check is one short line; an absent one says so.
    /// </remarks>
    private static string VerificationMessage(ObjectStorageVerification? verification) =>
        verification is { Findings.Count: > 0 }
            ? string.Join(" ", verification.Findings)
            : "Object storage could not be checked this deploy, so whether uploads work is unknown.";

    /// <summary>
    /// Every dependency manifest along the layout's search path. A .csproj name is not knowable up
    /// front, so those are listed by suffix rather than guessed at.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadManifestsAsync(
        string token, string owner, string repo, string branch,
        RepositoryLayout layout, CancellationToken cancellationToken)
    {
        var contents = new List<string>();

        foreach (var name in (string[])["package.json", "requirements.txt"])
        {
            var found = await _reader.FindAsync(token, owner, repo, branch, layout, name, cancellationToken);
            if (found is not null)
            {
                contents.Add(found.Content);
            }
        }

        var csprojs = await _reader.FindAllBySuffixAsync(
            token, owner, repo, branch, layout, ".csproj", cancellationToken);
        contents.AddRange(csprojs.Select(c => c.Content));

        return contents;
    }

    private static string Normalize(string? path) =>
        path?.Trim().Replace('\\', '/').Trim('/') ?? string.Empty;
}
