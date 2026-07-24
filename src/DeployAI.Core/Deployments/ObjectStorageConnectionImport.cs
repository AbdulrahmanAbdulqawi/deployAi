namespace DeployAI.Core.Deployments;

/// <summary>
/// Recovers an object-storage connection from the environment variables of an app that already
/// has one wired up.
///
/// Hetzner has no API for creating S3 credentials — they only come from their Console — so a
/// connection can never be provisioned from nothing. What it can be is *imported*: an app that
/// is already storing files has the endpoint and keys sitting in its deployment environment, and
/// copying them across beats retyping a secret key by hand.
/// </summary>
public static class ObjectStorageConnectionImport
{
    // Ordered by specificity. The compose-style STORAGE_* names come first because they are what
    // our own generated compose files use; the others cover apps wired up by hand.
    private static readonly string[][] EndpointKeys =
    [
        ["STORAGE_ENDPOINT", "Storage__Endpoint", "S3_ENDPOINT", "AWS_ENDPOINT_URL"]
    ];

    private static readonly string[][] RegionKeys =
    [
        ["STORAGE_REGION", "Storage__Region", "S3_REGION", "AWS_REGION"]
    ];

    private static readonly string[][] BucketKeys =
    [
        ["STORAGE_BUCKET", "Storage__Bucket", "S3_BUCKET", "AWS_BUCKET"]
    ];

    private static readonly string[][] AccessKeyKeys =
    [
        ["STORAGE_ACCESS_KEY", "Storage__AccessKey", "S3_ACCESS_KEY", "AWS_ACCESS_KEY_ID"]
    ];

    private static readonly string[][] SecretKeyKeys =
    [
        ["STORAGE_SECRET_KEY", "Storage__SecretKey", "S3_SECRET_KEY", "AWS_SECRET_ACCESS_KEY"]
    ];

    public sealed record Result(
        ObjectStorageConnection? Connection,
        IReadOnlyList<string> MissingKeys)
    {
        public bool Succeeded => Connection is not null;
    }

    /// <summary>
    /// Region is not required — it is only needed to sign requests, and a blank one still
    /// produces a usable connection. The other four are the fallback contract: without all of
    /// them the app silently writes to local disk instead.
    /// </summary>
    public static Result FromEnvironment(IReadOnlyList<(string Key, string? Value)> envVars)
    {
        var endpoint = Find(envVars, EndpointKeys[0]);
        var bucket = Find(envVars, BucketKeys[0]);
        var accessKey = Find(envVars, AccessKeyKeys[0]);
        var secretKey = Find(envVars, SecretKeyKeys[0]);
        var region = Find(envVars, RegionKeys[0]) ?? string.Empty;

        var missing = new List<string>();
        if (endpoint is null) missing.Add(EndpointKeys[0][0]);
        if (bucket is null) missing.Add(BucketKeys[0][0]);
        if (accessKey is null) missing.Add(AccessKeyKeys[0][0]);
        if (secretKey is null) missing.Add(SecretKeyKeys[0][0]);

        if (missing.Count > 0)
        {
            return new Result(null, missing);
        }

        return new Result(
            new ObjectStorageConnection(endpoint!, region, bucket!, accessKey!, secretKey!),
            []);
    }

    /// <summary>
    /// Coolify lists compose apps' variables twice — once at compose level and once per service —
    /// and only one copy carries the value. Take the first non-blank match rather than the first
    /// match, or an import silently picks up the empty twin.
    /// </summary>
    private static string? Find(IReadOnlyList<(string Key, string? Value)> envVars, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = envVars.FirstOrDefault(env =>
                string.Equals(env.Key, candidate, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(env.Value));

            if (!string.IsNullOrWhiteSpace(match.Value))
            {
                return match.Value.Trim();
            }
        }

        return null;
    }
}
