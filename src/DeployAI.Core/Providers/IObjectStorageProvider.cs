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
    /// Creates a bucket, or returns the existing one when the caller already owns a bucket by
    /// that name — provisioning runs on every deploy, so it has to be idempotent.
    /// </summary>
    Task<StorageBucket> CreateBucketAsync(
        ProviderCredentials credentials,
        string bucket,
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
