using System.Text.RegularExpressions;

namespace DeployAI.Infrastructure.GitHub;

/// <summary>
/// What a repository was read for, to decide whether the app stores files.
/// </summary>
/// <param name="ManifestContents">csproj / package.json / requirements.txt — an SDK dependency is
/// the strongest signal, because a project does not reference an S3 client by accident.</param>
public sealed record ObjectStorageScanInputs(
    string? AppsettingsContent = null,
    string? ComposeContent = null,
    string? DotEnvExampleContent = null,
    IReadOnlyList<string>? ManifestContents = null);

/// <summary>
/// Whether the app needs a bucket, and what said so.
/// </summary>
/// <remarks>
/// <paramref name="Evidence"/> is not decoration. Provisioning storage creates a real, billable
/// resource and writes five environment variables onto a live app; doing that on a guess, without
/// being able to say which line of which file prompted it, is not something a user can check or
/// argue with. Every deploy that provisions reports its reason.
/// </remarks>
public sealed record ObjectStorageNeed(bool Needed, IReadOnlyList<string> Evidence)
{
    public static readonly ObjectStorageNeed None = new(false, []);
}

public interface IObjectStorageNeedDetector
{
    ObjectStorageNeed Detect(ObjectStorageScanInputs inputs);
}

/// <summary>
/// Decides whether an app stores files, documents, images or video, so DeployAI can provision a
/// bucket and wire it before the app meets a request it cannot serve.
/// </summary>
/// <remarks>
/// Without this, object storage was something a user had to know they needed and ask for by name.
/// The failure it prevents is not a crash: an app whose storage is unconfigured falls back to the
/// container filesystem, accepts the upload, and loses it on the next redeploy — the file is gone
/// and nothing ever reported an error.
/// <para>
/// Detection is deliberately conservative. A bucket costs money and appears in someone's account,
/// so a single vague word is not enough: the signals below are things a project only contains if
/// it genuinely talks to object storage — an S3 client dependency, a configuration section shaped
/// like bucket credentials, or a MinIO service in its own compose file.
/// </para>
/// </remarks>
public sealed class ObjectStorageNeedDetector : IObjectStorageNeedDetector
{
    /// <summary>
    /// Package identifiers for an S3-compatible client, across the runtimes DeployAI deploys. A
    /// project referencing one of these is talking to object storage; nothing else pulls them in.
    /// </summary>
    private static readonly string[] ClientPackages =
    [
        "AWSSDK.S3",           // .NET
        "Minio",               // .NET / Python
        "@aws-sdk/client-s3",  // Node
        "aws-sdk",             // Node (v2)
        "multer-s3",           // Node upload middleware
        "boto3",               // Python
        "django-storages"      // Python
    ];

    /// <summary>
    /// Configuration keys that only appear when a bucket is being addressed. Bucket-and-key shapes
    /// only — a bare "Region" or "Endpoint" belongs to half the services in a compose file.
    /// </summary>
    private static readonly string[] ConfigMarkers =
    [
        "S3_BUCKET", "S3_ENDPOINT", "S3_ACCESS_KEY",
        "AWS_S3_BUCKET", "AWS_BUCKET",
        "STORAGE_BUCKET", "MINIO_ROOT_USER", "MINIO_ACCESS_KEY",
        "Storage__Bucket", "Storage__AccessKey"
    ];

    public ObjectStorageNeed Detect(ObjectStorageScanInputs inputs)
    {
        var evidence = new List<string>();

        foreach (var manifest in inputs.ManifestContents ?? [])
        {
            foreach (var package in ClientPackages)
            {
                if (!string.IsNullOrWhiteSpace(manifest) &&
                    manifest.Contains(package, StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add($"a project manifest references {package}");
                }
            }
        }

        // A "Storage" section carrying a bucket and a key is the .NET shape, and it is what the
        // app binds at startup — the same section whose absence makes it write to local disk.
        if (HasDotnetStorageSection(inputs.AppsettingsContent))
        {
            evidence.Add("appsettings.json declares a Storage section with a bucket and keys");
        }

        AddMarkerEvidence(inputs.AppsettingsContent, "appsettings.json", evidence);
        AddMarkerEvidence(inputs.DotEnvExampleContent, ".env.example", evidence);

        // A repo that runs MinIO for itself in development needs the real thing in production.
        if (!string.IsNullOrWhiteSpace(inputs.ComposeContent) &&
            Regex.IsMatch(inputs.ComposeContent, @"(^|\s|/)minio", RegexOptions.IgnoreCase))
        {
            evidence.Add("docker-compose runs a MinIO service");
        }

        AddMarkerEvidence(inputs.ComposeContent, "docker-compose", evidence);

        var distinct = evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count == 0 ? ObjectStorageNeed.None : new ObjectStorageNeed(true, distinct);
    }

    private static void AddMarkerEvidence(string? content, string source, List<string> evidence)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        foreach (var marker in ConfigMarkers)
        {
            if (content.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add($"{source} mentions {marker}");
            }
        }
    }

    /// <summary>
    /// A Storage section counts only when it names a bucket <em>and</em> a credential. "Storage"
    /// alone is too common a word — a persistent-volume note in a README would otherwise buy the
    /// user a bucket they never asked for.
    /// </summary>
    private static bool HasDotnetStorageSection(string? appsettings)
    {
        if (string.IsNullOrWhiteSpace(appsettings))
        {
            return false;
        }

        var section = Regex.Match(
            appsettings,
            @"""Storage""\s*:\s*\{(?<body>[^{}]*)\}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!section.Success)
        {
            return false;
        }

        var body = section.Groups["body"].Value;
        return body.Contains("Bucket", StringComparison.OrdinalIgnoreCase)
            && (body.Contains("AccessKey", StringComparison.OrdinalIgnoreCase)
                || body.Contains("SecretKey", StringComparison.OrdinalIgnoreCase));
    }
}
