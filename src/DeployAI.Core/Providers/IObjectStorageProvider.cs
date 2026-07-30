namespace DeployAI.Core.Providers;

public sealed record StorageBucket(
    string Name,
    DateTimeOffset? CreatedAt);

public sealed record StorageObject(
    string Key,
    long Size,
    DateTimeOffset? LastModified);

public sealed record StorageObjectPage(
    IReadOnlyList<StorageObject> Objects,
    string? NextContinuationToken,
    bool IsTruncated);

public sealed record StorageDeleteError(
    string Key,
    string Code,
    string Message);

public sealed record StorageDeleteResult(
    int DeletedCount,
    IReadOnlyList<StorageDeleteError> Errors);

/// <summary>
/// The result of writing, reading and deleting a probe object. <paramref name="FailedStep"/> names
/// which operation broke, because "storage is not working" and "storage can write but not read" send
/// you to completely different places.
/// </summary>
public sealed record StorageRoundTrip(
    bool Succeeded,
    string? FailedStep = null,
    string? Detail = null)
{
    public static readonly StorageRoundTrip Ok = new(true);
}

/// <summary>
/// Object storage (S3-compatible) management. This is a capability in its own right,
/// deliberately separate from <see cref="IDeploymentProvider"/>: a bucket is not somewhere
/// an application is deployed, so storage providers must never appear in deploy-target pickers.
/// </summary>
public interface IObjectStorageProvider
{
    string ProviderName { get; }

    string DisplayName { get; }

    Task<bool> ValidateCredentialsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes, reads back and deletes a small probe object, proving the bucket is actually usable
    /// with these credentials.
    /// </summary>
    /// <remarks>
    /// Listing buckets proves almost nothing: it succeeds while writes fail on a signature version
    /// the backend rejects, a checksum header it refuses, a payload it will not accept unsigned, or
    /// credentials that can read but not write. Each of those has cost a day, and each was found
    /// only when a person finally attempted an upload. A round trip is the smallest thing that
    /// exercises the same path an upload takes.
    /// </remarks>
    Task<StorageRoundTrip> VerifyRoundTripAsync(
        ProviderCredentials credentials,
        string bucket,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a bucket, or returns the existing one when the caller already owns a bucket by
    /// that name — provisioning runs on every deploy, so it has to be idempotent.
    /// </summary>
    Task<StorageBucket> CreateBucketAsync(
        ProviderCredentials credentials,
        string bucket,
        CancellationToken cancellationToken);

    /// <summary>
    /// Allows a browser at <paramref name="allowedOrigins"/> to upload to and read from the bucket.
    /// </summary>
    /// <remarks>
    /// A presigned upload URL is handed to the browser, which PUTs to the bucket directly — the
    /// server is not in that request's path, so the API's own CORS policy has no bearing on it. The
    /// bucket has its own, and a new bucket allows nothing: the preflight comes back 403 and the
    /// upload never starts. Every provisioned bucket therefore has to be told which site may use it.
    /// <para>
    /// Idempotent, like <see cref="CreateBucketAsync"/> — provisioning runs on every deploy, and a
    /// site's origin changes when its domain does.
    /// </para>
    /// </remarks>
    Task SetBucketCorsAsync(
        ProviderCredentials credentials,
        string bucket,
        IReadOnlyList<string> allowedOrigins,
        CancellationToken cancellationToken);

    Task<StorageObjectPage> ListObjectsAsync(
        ProviderCredentials credentials,
        string bucket,
        string? prefix,
        string? continuationToken,
        int maxKeys,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a batch of keys. Implementations reject batches larger than
    /// <see cref="ObjectStorageLimits.MaxDeleteBatchSize"/> rather than silently splitting them,
    /// so the caller keeps control of progress reporting.
    /// </summary>
    Task<StorageDeleteResult> DeleteObjectsAsync(
        ProviderCredentials credentials,
        string bucket,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken);

    /// <summary>
    /// A short-lived presigned URL, so object bytes are fetched straight from the
    /// storage endpoint instead of being proxied through this API.
    /// </summary>
    Task<string> GetDownloadUrlAsync(
        ProviderCredentials credentials,
        string bucket,
        string key,
        TimeSpan expiry,
        CancellationToken cancellationToken);
}

/// <summary>
/// Storage provider names. Deliberately kept apart from <c>ProviderNameValues</c>: those feed
/// deploy-target dispatch across the app, and a bucket is not a deploy target.
/// </summary>
public static class ObjectStorageProviderNames
{
    public const string Hetzner = "hetzner-storage";
}

public static class ObjectStorageLimits
{
    /// <summary>S3's own per-request cap for both ListObjectsV2 and DeleteObjects.</summary>
    public const int MaxDeleteBatchSize = 1000;

    public const int MaxListPageSize = 1000;

    public const int DefaultListPageSize = 100;
}

public interface IObjectStorageProviderFactory
{
    IObjectStorageProvider? GetObjectStorage(string providerName);

    IReadOnlyList<ObjectStorageProviderInfo> GetAvailableProviders();
}

public sealed record ObjectStorageProviderInfo(string Name, string DisplayName);
