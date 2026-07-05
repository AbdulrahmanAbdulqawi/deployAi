using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeployAI.Infrastructure.Railway;

public sealed record RailwayUserProfile(
    [property: JsonPropertyName("sub")] string? Sub,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("name")] string? Name);

public sealed record RailwayOAuthTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAtUtc);

public interface IRailwayOAuthService
{
    string BuildAuthorizationUrl(string state);
    Task<RailwayOAuthTokens> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken);
    Task<RailwayOAuthTokens> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
    Task<RailwayUserProfile> GetUserProfileAsync(string accessToken, CancellationToken cancellationToken);
}

public sealed class RailwayOAuthService : IRailwayOAuthService
{
    private readonly HttpClient _httpClient;
    private readonly RailwayOptions _railwayOptions;
    private readonly AppOptions _appOptions;

    public RailwayOAuthService(
        HttpClient httpClient,
        IOptions<RailwayOptions> railwayOptions,
        IOptions<AppOptions> appOptions)
    {
        _httpClient = httpClient;
        _railwayOptions = railwayOptions.Value;
        _appOptions = appOptions.Value;
    }

    public string BuildAuthorizationUrl(string state)
    {
        var redirectUri = Uri.EscapeDataString($"{_appOptions.ApiUrl.TrimEnd('/')}{_railwayOptions.CallbackPath}");
        var scope = Uri.EscapeDataString(_railwayOptions.Scopes);

        return "https://backboard.railway.com/oauth/auth" +
               $"?response_type=code" +
               $"&client_id={Uri.EscapeDataString(_railwayOptions.ClientId)}" +
               $"&redirect_uri={redirectUri}" +
               $"&scope={scope}" +
               $"&state={Uri.EscapeDataString(state)}" +
               "&prompt=consent";
    }

    public Task<RailwayOAuthTokens> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken) =>
        RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = $"{_appOptions.ApiUrl.TrimEnd('/')}{_railwayOptions.CallbackPath}"
        }, cancellationToken);

    public Task<RailwayOAuthTokens> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) =>
        RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        }, cancellationToken);

    public async Task<RailwayUserProfile> GetUserProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://backboard.railway.com/oauth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var profile = await response.Content.ReadFromJsonAsync<RailwayUserProfile>(cancellationToken);
        return profile ?? throw new InvalidOperationException("Railway profile response was empty.");
    }

    private async Task<RailwayOAuthTokens> RequestTokensAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://backboard.railway.com/oauth/token");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{_railwayOptions.ClientId}:{_railwayOptions.ClientSecret}")));
        request.Content = new FormUrlEncodedContent(form);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<RailwayTokenResponse>(cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new InvalidOperationException("Railway did not return an access token.");
        }

        var expiresIn = payload.ExpiresIn.GetValueOrDefault(3600);
        return new RailwayOAuthTokens(
            payload.AccessToken,
            payload.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)));
    }

    private sealed class RailwayTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }
    }
}

public static class RailwayOAuthTokenStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool IsOAuthPayload(string token) =>
        token.TrimStart().StartsWith("{", StringComparison.Ordinal);

    public static string Serialize(RailwayOAuthTokens tokens) =>
        JsonSerializer.Serialize(new StoredRailwayOAuthTokens
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            ExpiresAtUtc = tokens.ExpiresAtUtc
        }, JsonOptions);

    public static StoredRailwayOAuthTokens? TryParse(string token)
    {
        if (!IsOAuthPayload(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredRailwayOAuthTokens>(token, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed class StoredRailwayOAuthTokens
    {
        public string AccessToken { get; set; } = string.Empty;
        public string? RefreshToken { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}
