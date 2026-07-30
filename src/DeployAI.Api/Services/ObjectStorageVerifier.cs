using DeployAI.Core.Providers;

namespace DeployAI.Api.Services;

/// <summary>
/// What was proven about an app's storage, in the words the deploy log uses.
/// </summary>
/// <param name="Findings">One line per check. Always populated — a verification that reports
/// nothing is indistinguishable from one that never ran.</param>
public sealed record ObjectStorageVerification(
    bool Ok,
    IReadOnlyList<string> Findings);

public interface IObjectStorageVerifier
{
    Task<ObjectStorageVerification> VerifyAsync(
        IObjectStorageProvider provider,
        ProviderCredentials credentials,
        string endpoint,
        string bucket,
        IReadOnlyList<string> websiteOrigins,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exercises an app's object storage the way the app will, at the end of provisioning.
/// </summary>
/// <remarks>
/// Four storage failures in one day were each found only when a person finally attempted an upload:
/// presigned URLs signed SigV2 which the backend rejects; a payload the SDK left unsigned which it
/// also rejects; a bucket whose CORS allowed no origin, so the browser preflight returned 403; and
/// provisioning that ran only at bucket creation, so the CORS rule was never applied at all. Every
/// one of them passed every check DeployAI had, because those checks confirmed configuration rather
/// than behaviour.
/// <para>
/// These two do the smallest real things instead: a signed write-read-delete against the bucket, and
/// the exact preflight a browser sends. Between them they cover signing, checksums, credential
/// permissions and the CORS rule — for any app, in any language, since nothing here reads the app's
/// source.
/// </para>
/// </remarks>
public sealed class ObjectStorageVerifier : IObjectStorageVerifier
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ObjectStorageVerifier> _logger;

    public ObjectStorageVerifier(IHttpClientFactory httpClientFactory, ILogger<ObjectStorageVerifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ObjectStorageVerification> VerifyAsync(
        IObjectStorageProvider provider,
        ProviderCredentials credentials,
        string endpoint,
        string bucket,
        IReadOnlyList<string> websiteOrigins,
        CancellationToken cancellationToken)
    {
        var findings = new List<string>();
        var ok = true;

        var roundTrip = await provider.VerifyRoundTripAsync(credentials, bucket, cancellationToken);
        if (roundTrip.Succeeded)
        {
            findings.Add($"Storage verified: wrote, read and deleted a test object in '{bucket}'.");
        }
        else
        {
            ok = false;
            _logger.LogWarning(
                "Storage round trip failed at the {Step} step on bucket {Bucket}: {Detail}",
                roundTrip.FailedStep, bucket, roundTrip.Detail);
            findings.Add(
                $"Storage cannot {roundTrip.FailedStep} to '{bucket}': {roundTrip.Detail} " +
                "Uploads will fail until this is fixed.");
        }

        // What the app was just handed, said out loud. Hetzner issues S3 credentials per project and
        // they reach every bucket in it; scoping one to a single bucket needs a second key pair and a
        // bucket policy, and keys can only be created in Hetzner's console -- there is no API, so
        // DeployAI cannot mint one. It can at least refuse to let the blast radius stay invisible.
        try
        {
            var buckets = await provider.ListBucketsAsync(credentials, cancellationToken);
            if (buckets.Count > 1)
            {
                findings.Add(
                    $"Note: these credentials reach all {buckets.Count} buckets in the storage account, "
                    + $"not just '{bucket}'. To narrow that, give this app its own key pair in your "
                    + "provider's console and a bucket policy allowing only it.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not list buckets to report credential reach.");
        }

        foreach (var origin in websiteOrigins)
        {
            var cors = await CheckPreflightAsync(endpoint, bucket, origin, cancellationToken);
            if (cors.Allowed)
            {
                findings.Add($"Browser uploads allowed from {origin}.");
            }
            else
            {
                ok = false;
                findings.Add(
                    $"The bucket refuses browser uploads from {origin} ({cors.Detail}). " +
                    "A direct upload from the site will fail its preflight.");
            }
        }

        return new ObjectStorageVerification(ok, findings);
    }

    /// <summary>
    /// The exact request a browser sends before a cross-origin PUT: an OPTIONS carrying the origin,
    /// the method and the headers it intends to use. Anything other than an
    /// <c>Access-Control-Allow-Origin</c> in the response means the upload never starts.
    /// </summary>
    private async Task<(bool Allowed, string Detail)> CheckPreflightAsync(
        string endpoint,
        string bucket,
        string origin,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint?.TrimEnd('/'), UriKind.Absolute, out var baseUri))
        {
            return (false, "the storage endpoint is not a valid absolute URL");
        }

        // Path-style, matching how the app addresses the bucket: Hetzner rejects virtual-host style.
        var probe = new Uri(baseUri, $"/{bucket}/deployai-preflight-probe");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, probe);
            request.Headers.TryAddWithoutValidation("Origin", origin);
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "PUT");
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type");

            var client = _httpClientFactory.CreateClient(nameof(ObjectStorageVerifier));
            client.Timeout = TimeSpan.FromSeconds(15);

            using var response = await client.SendAsync(request, cancellationToken);

            return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed)
                ? (true, string.Join(",", allowed))
                : (false, $"no Access-Control-Allow-Origin, HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not preflight {Probe} for origin {Origin}.", probe, origin);
            return (false, ex.Message);
        }
    }
}
