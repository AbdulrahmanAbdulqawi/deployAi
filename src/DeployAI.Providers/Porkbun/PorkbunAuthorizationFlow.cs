using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Porkbun;

/// <summary>
/// Gets Porkbun credentials by asking the account holder to approve DeployAI, rather than asking
/// them to create a key pair and paste it.
/// </summary>
/// <remarks>
/// Porkbun's flow needs no redirect URI: DeployAI starts a request, sends the user to an approval
/// URL, and polls. That makes it usable from a local development instance and a deployed one
/// without configuring anything, unlike a conventional OAuth callback.
/// <para>
/// PKCE is what makes the poll safe. Without a code challenge Porkbun returns only the public key;
/// with one it returns both, exactly once, and only to whoever holds the verifier.
/// </para>
/// </remarks>
public sealed class PorkbunAuthorizationFlow : IDnsAuthorizationFlow
{
    /// <summary>Porkbun expires an approval URL ten minutes after issuing it.</summary>
    private static readonly TimeSpan ApprovalWindow = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;

    public PorkbunAuthorizationFlow(HttpClient httpClient) => _httpClient = httpClient;

    public string ProviderName => "porkbun";

    public async Task<DnsAuthorizationRequest> BeginAsync(CancellationToken cancellationToken)
    {
        var verifier = CreateVerifier();

        var response = await PostAsync<PorkbunAuthorizationStart>(
            "apikey/request",
            new Dictionary<string, object?>
            {
                ["name"] = "DeployAI",
                ["codeChallenge"] = Challenge(verifier),
                ["codeChallengeMethod"] = "S256"
            },
            cancellationToken);

        if (response is null || string.IsNullOrWhiteSpace(response.RequestToken) ||
            string.IsNullOrWhiteSpace(response.AuthUrl))
        {
            throw new DeployAIException(
                "porkbun_api_error",
                "Porkbun did not start the approval. Try again, or connect with API keys instead.");
        }

        return new DnsAuthorizationRequest(
            response.RequestToken,
            response.AuthUrl,
            verifier,
            DateTimeOffset.UtcNow.Add(ApprovalWindow));
    }

    public async Task<DnsAuthorizationResult> PollAsync(
        DnsAuthorizationRequest request, CancellationToken cancellationToken)
    {
        // Checked before asking, so a window that closed reads as expired rather than as a denial.
        if (DateTimeOffset.UtcNow > request.ExpiresAt)
        {
            return new DnsAuthorizationResult(
                DnsAuthorizationState.Expired,
                "That approval link expired. Start again and approve it within ten minutes.");
        }

        HttpResponseMessage response;
        string raw;
        try
        {
            response = await _httpClient.PostAsync(
                $"{PorkbunDnsProvider.ApiBase}/apikey/retrieve",
                JsonContent.Create(new Dictionary<string, object?>
                {
                    ["requestToken"] = request.RequestToken,
                    ["codeVerifier"] = request.Verifier
                }),
                cancellationToken);
            raw = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            return new DnsAuthorizationResult(
                DnsAuthorizationState.Unreachable,
                "We could not reach Porkbun to check the approval. Still waiting — try again shortly.");
        }

        // A denial is its own status code, and must not read as "still waiting" forever.
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new DnsAuthorizationResult(
                DnsAuthorizationState.Denied,
                "The request was declined in Porkbun. Start again if that was not what you meant.");
        }

        PorkbunAuthorizationPoll? poll = null;
        try
        {
            poll = JsonSerializer.Deserialize<PorkbunAuthorizationPoll>(raw);
        }
        catch (JsonException)
        {
            // Handled by the status checks below.
        }

        if (string.Equals(poll?.Status, "PENDING", StringComparison.OrdinalIgnoreCase))
        {
            return new DnsAuthorizationResult(
                DnsAuthorizationState.Pending,
                poll?.Message ?? "Waiting for you to approve it in Porkbun.");
        }

        if (string.Equals(poll?.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(poll.ApiKey) &&
            !string.IsNullOrWhiteSpace(poll.SecretApiKey))
        {
            // Porkbun returns the secret exactly once. The caller must store this before doing
            // anything else that could fail; there is no second chance to ask.
            return new DnsAuthorizationResult(
                DnsAuthorizationState.Approved,
                "Approved.",
                new ProviderCredentials(
                    PorkbunCredentialStorage.Serialize(poll.ApiKey!, poll.SecretApiKey!)));
        }

        // A 400 here means the token expired or was already used, not that the user did anything.
        return new DnsAuthorizationResult(
            DnsAuthorizationState.Expired,
            string.IsNullOrWhiteSpace(poll?.Message)
                ? "That approval link is no longer valid. Start again."
                : poll.Message!);
    }

    /// <summary>A 43-character base64url verifier, per RFC 7636.</summary>
    private static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<T?> PostAsync<T>(
        string path, Dictionary<string, object?> body, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.PostAsync(
                $"{PorkbunDnsProvider.ApiBase}/{path}", JsonContent.Create(body), cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new DeployAIException(
                    DnsErrorCodes.RateLimited,
                    "Porkbun is limiting requests right now. Nothing was started — try again shortly.");
            }

            return JsonSerializer.Deserialize<T>(raw);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            throw new DeployAIException(
                DnsErrorCodes.Unreachable,
                "We could not reach Porkbun just now, so nothing was started. Try again in a moment.");
        }
    }

    private sealed class PorkbunAuthorizationStart
    {
        [JsonPropertyName("requestToken")]
        public string? RequestToken { get; set; }

        [JsonPropertyName("authUrl")]
        public string? AuthUrl { get; set; }

        [JsonPropertyName("deliveryMode")]
        public string? DeliveryMode { get; set; }
    }

    private sealed class PorkbunAuthorizationPoll
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("apikey")]
        public string? ApiKey { get; set; }

        [JsonPropertyName("secretapikey")]
        public string? SecretApiKey { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
