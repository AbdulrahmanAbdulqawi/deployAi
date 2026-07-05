using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Infrastructure.GitHub;

public sealed record GitHubUserProfile(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("email")] string? Email);

public sealed record GitHubRepo(
    [property: JsonPropertyName("full_name")] string FullName,
    [property: JsonPropertyName("default_branch")] string DefaultBranch,
    [property: JsonPropertyName("private")] bool Private);

public sealed record GitHubBranch(
    [property: JsonPropertyName("name")] string Name);

public sealed record GitHubContentItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type);

public sealed record GitHubFileContent(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("encoding")] string Encoding);

public sealed record GitHubTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken);

public sealed record GitHubCommitHead(
    [property: JsonPropertyName("sha")] string Sha);

public interface IGitHubService
{
    string BuildAuthorizationUrl(string state);
    Task<string> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken);
    Task<GitHubUserProfile> GetUserProfileAsync(string accessToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<GitHubRepo>> ListReposAsync(string accessToken, int page, int perPage, string? search, CancellationToken cancellationToken);
    Task<IReadOnlyList<GitHubBranch>> ListBranchesAsync(string accessToken, string owner, string repo, CancellationToken cancellationToken);
    Task<IReadOnlyList<GitHubContentItem>> ListContentsAsync(string accessToken, string owner, string repo, string? path, string? gitRef, CancellationToken cancellationToken);
    Task<IReadOnlyList<GitHubContentItem>> ListAllContentsAsync(string accessToken, string owner, string repo, string? path, string? gitRef, CancellationToken cancellationToken);
    Task<string?> GetFileContentAsync(string accessToken, string owner, string repo, string path, string? gitRef, CancellationToken cancellationToken);
    Task<string?> GetBranchHeadShaAsync(string accessToken, string owner, string repo, string branch, CancellationToken cancellationToken);
}

public sealed class GitHubService : IGitHubService
{
    private readonly HttpClient _httpClient;
    private readonly DeployAI.Infrastructure.Options.GitHubOptions _options;
    private readonly DeployAI.Infrastructure.Options.AppOptions _appOptions;

    public GitHubService(
        HttpClient httpClient,
        Microsoft.Extensions.Options.IOptions<DeployAI.Infrastructure.Options.GitHubOptions> options,
        Microsoft.Extensions.Options.IOptions<DeployAI.Infrastructure.Options.AppOptions> appOptions)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _appOptions = appOptions.Value;
    }

    public string BuildAuthorizationUrl(string state)
    {
        var redirectUri = Uri.EscapeDataString($"{_appOptions.ApiUrl.TrimEnd('/')}{_options.CallbackPath}");
        return $"https://github.com/login/oauth/authorize?client_id={_options.ClientId}&redirect_uri={redirectUri}&scope=repo&state={Uri.EscapeDataString(state)}";
    }

    public async Task<string> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new
        {
            client_id = _options.ClientId,
            client_secret = _options.ClientSecret,
            code,
            redirect_uri = $"{_appOptions.ApiUrl.TrimEnd('/')}{_options.CallbackPath}"
        });

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<GitHubTokenResponse>(cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("GitHub did not return an access token.");
        }

        return token.AccessToken;
    }

    public async Task<GitHubUserProfile> GetUserProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "https://api.github.com/user", accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var profile = await response.Content.ReadFromJsonAsync<GitHubUserProfile>(cancellationToken);
        return profile ?? throw new InvalidOperationException("GitHub profile response was empty.");
    }

    public async Task<IReadOnlyList<GitHubRepo>> ListReposAsync(string accessToken, int page, int perPage, string? search, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/user/repos?sort=updated&per_page={perPage}&page={page}";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var repos = await response.Content.ReadFromJsonAsync<List<GitHubRepo>>(cancellationToken) ?? [];
        if (!string.IsNullOrWhiteSpace(search))
        {
            repos = repos
                .Where(r => r.FullName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return repos;
    }

    public async Task<IReadOnlyList<GitHubBranch>> ListBranchesAsync(string accessToken, string owner, string repo, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/branches?per_page=100";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<GitHubBranch>>(cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<GitHubContentItem>> ListContentsAsync(
        string accessToken,
        string owner,
        string repo,
        string? path,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeContentPath(path);
        var pathSegment = string.IsNullOrEmpty(normalizedPath)
            ? string.Empty
            : $"/{Uri.EscapeDataString(normalizedPath)}";
        var refQuery = string.IsNullOrWhiteSpace(gitRef)
            ? string.Empty
            : $"?ref={Uri.EscapeDataString(gitRef)}";
        var url = $"https://api.github.com/repos/{owner}/{repo}/contents{pathSegment}{refQuery}";

        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("GitHub is busy right now. Wait a moment and try again.");
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<List<GitHubContentItem>>(cancellationToken) ?? [];
        return payload
            .Where(item => string.Equals(item.Type, "dir", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GitHubContentItem>> ListAllContentsAsync(
        string accessToken,
        string owner,
        string repo,
        string? path,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeContentPath(path);
        var pathSegment = string.IsNullOrEmpty(normalizedPath)
            ? string.Empty
            : $"/{Uri.EscapeDataString(normalizedPath)}";
        var refQuery = string.IsNullOrWhiteSpace(gitRef)
            ? string.Empty
            : $"?ref={Uri.EscapeDataString(gitRef)}";
        var url = $"https://api.github.com/repos/{owner}/{repo}/contents{pathSegment}{refQuery}";

        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("GitHub is busy right now. Wait a moment and try again.");
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<List<GitHubContentItem>>(cancellationToken) ?? [];
        return payload
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> GetFileContentAsync(
        string accessToken,
        string owner,
        string repo,
        string path,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeContentPath(path);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return null;
        }

        var refQuery = string.IsNullOrWhiteSpace(gitRef)
            ? string.Empty
            : $"?ref={Uri.EscapeDataString(gitRef)}";
        var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{Uri.EscapeDataString(normalizedPath)}{refQuery}";

        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<GitHubFileContent>(cancellationToken);
        if (payload is null || !string.Equals(payload.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var bytes = Convert.FromBase64String(payload.Content.Replace("\n", string.Empty, StringComparison.Ordinal));
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public async Task<string?> GetBranchHeadShaAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        var url = $"https://api.github.com/repos/{owner}/{repo}/commits/{Uri.EscapeDataString(branch.Trim())}";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var commit = await response.Content.ReadFromJsonAsync<GitHubCommitHead>(cancellationToken);
        return string.IsNullOrWhiteSpace(commit?.Sha) ? null : commit.Sha;
    }

    private static string NormalizeContentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or "/")
        {
            return string.Empty;
        }

        return path.Trim().Trim('/');
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("DeployAI");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }
}
