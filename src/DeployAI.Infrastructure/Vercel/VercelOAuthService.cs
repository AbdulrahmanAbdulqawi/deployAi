using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeployAI.Infrastructure.Vercel;

public sealed record VercelUserProfile(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("email")] string? Email);

public interface IVercelOAuthService
{
    string BuildAuthorizationUrl(string state);
    Task<string> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken);
    Task<VercelUserProfile> GetUserProfileAsync(string accessToken, CancellationToken cancellationToken);
}

public sealed class VercelOAuthService : IVercelOAuthService
{
    private readonly HttpClient _httpClient;
    private readonly VercelOptions _vercelOptions;
    private readonly AppOptions _appOptions;

    public VercelOAuthService(
        HttpClient httpClient,
        IOptions<VercelOptions> vercelOptions,
        IOptions<AppOptions> appOptions)
    {
        _httpClient = httpClient;
        _vercelOptions = vercelOptions.Value;
        _appOptions = appOptions.Value;
    }

    public string BuildAuthorizationUrl(string state)
    {
        var redirectUri = Uri.EscapeDataString($"{_appOptions.ApiUrl.TrimEnd('/')}{_vercelOptions.CallbackPath}");
        var slug = string.IsNullOrWhiteSpace(_vercelOptions.IntegrationSlug)
            ? _vercelOptions.ClientId
            : _vercelOptions.IntegrationSlug;

        return $"https://vercel.com/integrations/{slug}/new" +
               $"?state={Uri.EscapeDataString(state)}" +
               $"&redirect_uri={redirectUri}";
    }

    public async Task<string> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken)
    {
        var redirectUri = $"{_appOptions.ApiUrl.TrimEnd('/')}{_vercelOptions.CallbackPath}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vercel.com/v2/oauth/access_token");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _vercelOptions.ClientId,
            ["client_secret"] = _vercelOptions.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        });

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<VercelTokenResponse>(cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new InvalidOperationException("Vercel did not return an access token.");
        }

        return payload.AccessToken;
    }

    public async Task<VercelUserProfile> GetUserProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vercel.com/v2/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var profile = await response.Content.ReadFromJsonAsync<VercelUserProfile>(cancellationToken);
        return profile ?? throw new InvalidOperationException("Vercel profile response was empty.");
    }

    private sealed class VercelTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("installation_id")]
        public string? InstallationId { get; set; }
    }
}
