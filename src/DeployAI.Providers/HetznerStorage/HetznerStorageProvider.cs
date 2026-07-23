using Amazon.S3;
using Amazon.S3.Model;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.HetznerStorage;

/// <summary>
/// S3-compatible object storage, targeting Hetzner Object Storage.
///
/// Note this implements <see cref="IObjectStorageProvider"/> only — it is deliberately not an
/// <see cref="IDeploymentProvider"/> and its name is not in the <c>ProviderName</c> enum, so it
/// can never surface in a deploy-target picker.
/// </summary>
public sealed class HetznerStorageProvider : IObjectStorageProvider
{
    private readonly Amazon.Runtime.HttpClientFactory? _httpClientFactory;

    /// <param name="httpClientFactory">
    /// Test seam only — lets tests supply a mocked transport. Null in production, where the
    /// AWS SDK manages its own connections.
    /// </param>
    public HetznerStorageProvider(Amazon.Runtime.HttpClientFactory? httpClientFactory = null)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string ProviderName => ObjectStorageProviderNames.Hetzner;

    public string DisplayName => "Hetzner Object Storage";

    public async Task<bool> ValidateCredentialsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(credentials);
            await client.ListBucketsAsync(cancellationToken);
            return true;
        }
        catch (AmazonS3Exception)
        {
            return false;
        }
        catch (DeployAIException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials);
        var response = await client.ListBucketsAsync(cancellationToken);

        return response.Buckets is null
            ? []
            : response.Buckets
                .Select(b => new StorageBucket(b.BucketName, b.CreationDate))
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    public async Task<StorageObjectPage> ListObjectsAsync(
        ProviderCredentials credentials,
        string bucket,
        string? prefix,
        string? continuationToken,
        int maxKeys,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials);

        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix,
            ContinuationToken = string.IsNullOrWhiteSpace(continuationToken) ? null : continuationToken,
            MaxKeys = Math.Clamp(maxKeys, 1, ObjectStorageLimits.MaxListPageSize)
        }, cancellationToken);

        var objects = response.S3Objects is null
            ? []
            : response.S3Objects
                .Select(o => new StorageObject(o.Key, o.Size ?? 0, o.LastModified))
                .ToList();

        return new StorageObjectPage(
            objects,
            response.NextContinuationToken,
            response.IsTruncated ?? false);
    }

    public async Task<StorageDeleteResult> DeleteObjectsAsync(
        ProviderCredentials credentials,
        string bucket,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return new StorageDeleteResult(0, []);
        }

        // Reject rather than silently splitting, so the caller keeps control of progress.
        if (keys.Count > ObjectStorageLimits.MaxDeleteBatchSize)
        {
            throw new DeployAIException(
                "storage_delete_batch_too_large",
                $"Delete up to {ObjectStorageLimits.MaxDeleteBatchSize} objects at a time.");
        }

        using var client = CreateClient(credentials);

        var request = new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = keys.Select(k => new KeyVersion { Key = k }).ToList()
        };

        try
        {
            var response = await client.DeleteObjectsAsync(request, cancellationToken);
            return ToResult(response.DeletedObjects, response.DeleteErrors);
        }
        catch (DeleteObjectsException ex)
        {
            // The SDK throws when *any* key fails, even though the rest were deleted.
            // Partial success is normal here (e.g. one locked object), so report both
            // sides rather than failing the whole batch.
            return ToResult(ex.Response.DeletedObjects, ex.Response.DeleteErrors);
        }
    }

    private static StorageDeleteResult ToResult(
        List<DeletedObject>? deleted,
        List<DeleteError>? errors)
    {
        var mappedErrors = errors is null
            ? []
            : errors.Select(e => new StorageDeleteError(e.Key, e.Code, e.Message)).ToList();

        return new StorageDeleteResult(deleted?.Count ?? 0, mappedErrors);
    }

    public Task<string> GetDownloadUrlAsync(
        ProviderCredentials credentials,
        string bucket,
        string key,
        TimeSpan expiry,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials);

        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry)
        });

        return Task.FromResult(url);
    }

    /// <summary>
    /// Hetzner needs path-style addressing, and rejects the SDK's default CRC32 checksum
    /// headers with InvalidArgument — both settings are required for any call to succeed.
    /// </summary>
    private AmazonS3Client CreateClient(ProviderCredentials credentials)
    {
        var payload = StorageCredentialStorage.TryParse(credentials.Token)
            ?? throw new DeployAIException(
                "storage_credentials_invalid",
                "Your storage connection is missing its endpoint or keys. Reconnect it in settings.");

        var config = new AmazonS3Config
        {
            ServiceURL = payload.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = payload.Region,
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED
        };

        if (_httpClientFactory is not null)
        {
            config.HttpClientFactory = _httpClientFactory;
        }

        return new AmazonS3Client(payload.AccessKey, payload.SecretKey, config);
    }
}
