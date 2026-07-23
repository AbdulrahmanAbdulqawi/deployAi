namespace DeployAI.Core.Deployments;

/// <summary>
/// The bucket connection an app needs to talk to object storage.
/// </summary>
public sealed record ObjectStorageConnection(
    string Endpoint,
    string Region,
    string Bucket,
    string AccessKey,
    string SecretKey);

/// <summary>
/// Turns a provisioned bucket into the environment variables an app reads.
///
/// This is also where the S3-compatibility quirks live. They are not incidental: an app that
/// gets them wrong fails at runtime with errors that point nowhere near the cause, and each one
/// below cost real debugging in a previous deployment. Keeping them here means the next app
/// inherits the answers instead of rediscovering them.
/// </summary>
public static class ObjectStorageEnvironmentWiring
{
    /// <summary>
    /// Hetzner's endpoint rejects virtual-host-style addressing, so the SDK must be told to put
    /// the bucket in the path.
    /// </summary>
    public const string RequiresPathStyleAddressing = "ForcePathStyle = true";

    /// <summary>
    /// The AWS SDK adds CRC32 checksum headers by default; Hetzner answers InvalidArgument.
    /// Both request and response checksums have to drop to WHEN_REQUIRED.
    /// </summary>
    public const string RequiresChecksumWhenRequired =
        "RequestChecksumCalculation / ResponseChecksumValidation = WHEN_REQUIRED";

    /// <summary>
    /// Hetzner rejects UNSIGNED-PAYLOAD, so uploads must come from a seekable stream the SDK
    /// can hash — buffer a forward-only request body before handing it over.
    /// </summary>
    public const string RequiresSeekableUploadStreams = "Buffer non-seekable upload streams";

    /// <summary>
    /// A region is required for SigV4 signing even though Hetzner ignores it for routing. It is
    /// not the switch between S3 and local disk — that is the four-value check below.
    /// </summary>
    public const string RegionIsForSigningOnly = "Region is required to sign, not to route";

    public static IReadOnlyList<string> Quirks =>
    [
        RequiresPathStyleAddressing,
        RequiresChecksumWhenRequired,
        RequiresSeekableUploadStreams,
        RegionIsForSigningOnly
    ];

    /// <summary>
    /// The fallback contract: an app switches to object storage only when endpoint, bucket,
    /// access key and secret key are all present. Any blank means local disk, which is why a
    /// half-configured deployment silently writes to a container filesystem that disappears on
    /// the next redeploy rather than failing loudly.
    /// </summary>
    public static bool IsComplete(ObjectStorageConnection? connection) =>
        connection is not null &&
        !string.IsNullOrWhiteSpace(connection.Endpoint) &&
        !string.IsNullOrWhiteSpace(connection.Bucket) &&
        !string.IsNullOrWhiteSpace(connection.AccessKey) &&
        !string.IsNullOrWhiteSpace(connection.SecretKey);

    /// <summary>
    /// Keys are framework-shaped: .NET reads <c>Storage__*</c> through configuration binding,
    /// while Node and Python conventionally use flat S3_* names.
    /// </summary>
    public static IReadOnlyList<ObjectStorageEnvAssignment> BuildAssignments(
        string? serverFramework,
        ObjectStorageConnection connection)
    {
        if (!IsComplete(connection))
        {
            throw new ArgumentException(
                "Object storage env vars must not be written from an incomplete connection — " +
                "a blank value silently downgrades the app to local disk.",
                nameof(connection));
        }

        var names = ResolveKeyNames(serverFramework);

        return
        [
            new(names.Endpoint, connection.Endpoint, IsSecret: false),
            new(names.Region, connection.Region, IsSecret: false),
            new(names.Bucket, connection.Bucket, IsSecret: false),
            new(names.AccessKey, connection.AccessKey, IsSecret: true),
            new(names.SecretKey, connection.SecretKey, IsSecret: true)
        ];
    }

    private static StorageKeyNames ResolveKeyNames(string? serverFramework) =>
        serverFramework?.Trim().ToLowerInvariant() switch
        {
            "dotnet" or "aspnet" or "aspnetcore" or "docker" or null or "" => new StorageKeyNames(
                "Storage__Endpoint",
                "Storage__Region",
                "Storage__Bucket",
                "Storage__AccessKey",
                "Storage__SecretKey"),
            _ => new StorageKeyNames(
                "S3_ENDPOINT",
                "S3_REGION",
                "S3_BUCKET",
                "S3_ACCESS_KEY",
                "S3_SECRET_KEY")
        };

    private sealed record StorageKeyNames(
        string Endpoint,
        string Region,
        string Bucket,
        string AccessKey,
        string SecretKey);
}

/// <summary>
/// <paramref name="IsSecret"/> drives how the value is stored on the provider — access keys
/// must not be readable back out of the deployment platform's UI.
/// </summary>
public sealed record ObjectStorageEnvAssignment(string Key, string Value, bool IsSecret);
