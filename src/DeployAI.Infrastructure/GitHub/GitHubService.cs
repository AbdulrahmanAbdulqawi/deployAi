using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Exceptions;

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
    [property: JsonPropertyName("encoding")] string Encoding,
    [property: JsonPropertyName("sha")] string? Sha);

public sealed record GitHubFileMetadata(string Content, string? Sha);

public sealed record GitHubTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken);

public sealed record GitHubCommitHead(
    [property: JsonPropertyName("sha")] string Sha);

public sealed record GitHubContentCommit(
    [property: JsonPropertyName("sha")] string? Sha);

public sealed record GitHubContentUpsertResponse(
    [property: JsonPropertyName("commit")] GitHubContentCommit? Commit);

public sealed record GitHubRefResponse(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("object")] GitHubRefObject Object);

public sealed record GitHubRefObject(
    [property: JsonPropertyName("sha")] string Sha);

public sealed record GitHubCreateRefRequest(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("sha")] string Sha);

public sealed record GitHubTreeBlob(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] string Content);

public sealed record GitHubTreeRequest(
    [property: JsonPropertyName("base_tree")] string BaseTree,
    [property: JsonPropertyName("tree")] IReadOnlyList<GitHubTreeBlob> Tree);

public sealed record GitHubTreeResponse(
    [property: JsonPropertyName("sha")] string Sha);

public sealed record GitHubCommitRequest(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("tree")] string Tree,
    [property: JsonPropertyName("parents")] IReadOnlyList<string> Parents);

public sealed record GitHubCommitResponse(
    [property: JsonPropertyName("sha")] string Sha);

public sealed record GitHubUpdateRefRequest(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("force")] bool Force);

public sealed record GitHubPullRequestResponse(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("html_url")] string HtmlUrl);

public sealed record GitHubPullRequestRequest(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("body")] string Body);

public sealed record GitHubMergeRequest(
    [property: JsonPropertyName("commit_message")] string CommitMessage);

public sealed record GitHubCommitInfo(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("commit")] GitHubCommitDetails Commit);

public sealed record GitHubHookResponse(
    [property: JsonPropertyName("id")] long Id);

public sealed record GitHubCommitDetails(
    [property: JsonPropertyName("message")] string Message);

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
    Task<GitHubFileMetadata?> GetFileMetadataAsync(string accessToken, string owner, string repo, string path, string? gitRef, CancellationToken cancellationToken);
    Task<string?> UpsertFileAsync(
        string accessToken,
        string owner,
        string repo,
        string path,
        string content,
        string commitMessage,
        string branch,
        string? existingSha,
        CancellationToken cancellationToken);
    Task<GitHubRepo?> GetRepositoryAsync(string accessToken, string owner, string repo, CancellationToken cancellationToken);
    Task<string?> GetBranchHeadShaAsync(string accessToken, string owner, string repo, string branch, CancellationToken cancellationToken);
    Task<string?> CreateBranchAsync(string accessToken, string owner, string repo, string branchName, string baseSha, CancellationToken cancellationToken);
    Task<string?> CommitFilesAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        string commitMessage,
        IReadOnlyList<(string Path, string Content)> files,
        CancellationToken cancellationToken);
    Task<GitHubPullRequestResponse?> CreatePullRequestAsync(
        string accessToken,
        string owner,
        string repo,
        string title,
        string headBranch,
        string baseBranch,
        string body,
        CancellationToken cancellationToken);
    Task<bool> MergePullRequestAsync(
        string accessToken,
        string owner,
        string repo,
        int pullRequestNumber,
        string commitMessage,
        CancellationToken cancellationToken);
    Task<GitHubCommitInfo?> GetCommitAsync(
        string accessToken,
        string owner,
        string repo,
        string sha,
        CancellationToken cancellationToken);
    Task<Stream> DownloadRepositoryZipballAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        CancellationToken cancellationToken);
    Task<long> CreateRepoWebhookAsync(
        string accessToken,
        string owner,
        string repo,
        string webhookUrl,
        string secret,
        CancellationToken cancellationToken);
    Task DeleteRepoWebhookAsync(
        string accessToken,
        string owner,
        string repo,
        long hookId,
        CancellationToken cancellationToken);
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
        EnsureGitHubSuccess(response);
        var profile = await response.Content.ReadFromJsonAsync<GitHubUserProfile>(cancellationToken);        return profile ?? throw new InvalidOperationException("GitHub profile response was empty.");
    }

    public async Task<IReadOnlyList<GitHubRepo>> ListReposAsync(string accessToken, int page, int perPage, string? search, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/user/repos?sort=updated&per_page={perPage}&page={page}";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        EnsureGitHubSuccess(response);
        var repos = await response.Content.ReadFromJsonAsync<List<GitHubRepo>>(cancellationToken) ?? [];        if (!string.IsNullOrWhiteSpace(search))
        {
            repos = repos
                .Where(r => r.FullName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return repos;
    }

    public async Task<GitHubRepo?> GetRepositoryAsync(
        string accessToken,
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureGitHubSuccess(response);
        return await response.Content.ReadFromJsonAsync<GitHubRepo>(cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubBranch>> ListBranchesAsync(string accessToken, string owner, string repo, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/branches?per_page=100";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        EnsureGitHubSuccess(response);
        return await response.Content.ReadFromJsonAsync<List<GitHubBranch>>(cancellationToken) ?? [];    }

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

        EnsureGitHubSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<List<GitHubContentItem>>(cancellationToken) ?? [];
        return payload
            .Where(item => string.Equals(item.Type, "dir", StringComparison.OrdinalIgnoreCase))            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
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

        EnsureGitHubSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<List<GitHubContentItem>>(cancellationToken) ?? [];
        return payload
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)            .ToList();
    }

    public async Task<string?> GetFileContentAsync(
        string accessToken,
        string owner,
        string repo,
        string path,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        var metadata = await GetFileMetadataAsync(accessToken, owner, repo, path, gitRef, cancellationToken);
        var content = metadata?.Content;
        // Strip a leading UTF-8 BOM: files written by Visual Studio / PowerShell carry one, and it
        // breaks every text consumer downstream (JSON parsing throws, string.StartsWith misses).
        return content is { Length: > 0 } && content[0] == '﻿' ? content[1..] : content;
    }

    public async Task<GitHubFileMetadata?> GetFileMetadataAsync(
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
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureGitHubSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<GitHubFileContent>(cancellationToken);
        if (payload is null || !string.Equals(payload.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var bytes = Convert.FromBase64String(payload.Content.Replace("\n", string.Empty, StringComparison.Ordinal));
        return new GitHubFileMetadata(System.Text.Encoding.UTF8.GetString(bytes), payload.Sha);
    }

    public async Task<string?> UpsertFileAsync(
        string accessToken,
        string owner,
        string repo,
        string path,
        string content,
        string commitMessage,
        string branch,
        string? existingSha,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeContentPath(path);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            throw new ArgumentException("A repository file path is required.", nameof(path));
        }

        var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{Uri.EscapeDataString(normalizedPath)}";
        var body = new Dictionary<string, object?>
        {
            ["message"] = commitMessage,
            ["content"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content)),
            ["branch"] = branch
        };

        if (!string.IsNullOrWhiteSpace(existingSha))
        {
            body["sha"] = existingSha;
        }

        using var request = CreateAuthorizedRequest(HttpMethod.Put, url, accessToken);
        request.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        EnsureGitHubSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<GitHubContentUpsertResponse>(cancellationToken);
        return string.IsNullOrWhiteSpace(payload?.Commit?.Sha) ? null : payload.Commit.Sha;
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
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureGitHubSuccess(response);
        var commit = await response.Content.ReadFromJsonAsync<GitHubCommitHead>(cancellationToken);
        return string.IsNullOrWhiteSpace(commit?.Sha) ? null : commit.Sha;
    }

    public async Task<string?> CreateBranchAsync(
        string accessToken,
        string owner,
        string repo,
        string branchName,
        string baseSha,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/git/refs";
        using var request = CreateAuthorizedRequest(HttpMethod.Post, url, accessToken);
        request.Content = JsonContent.Create(new GitHubCreateRefRequest($"refs/heads/{branchName}", baseSha));
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            return null;
        }

        EnsureGitHubSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<GitHubRefResponse>(cancellationToken);
        return payload?.Object.Sha;
    }

    public async Task<string?> CommitFilesAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        string commitMessage,
        IReadOnlyList<(string Path, string Content)> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return await GetBranchHeadShaAsync(accessToken, owner, repo, branch, cancellationToken);
        }

        var baseSha = await GetBranchHeadShaAsync(accessToken, owner, repo, branch, cancellationToken);
        if (string.IsNullOrWhiteSpace(baseSha))
        {
            return null;
        }

        var refUrl = $"https://api.github.com/repos/{owner}/{repo}/git/refs/heads/{Uri.EscapeDataString(branch)}";
        using var refRequest = CreateAuthorizedRequest(HttpMethod.Get, refUrl, accessToken);
        var refResponse = await _httpClient.SendAsync(refRequest, cancellationToken);
        EnsureGitHubSuccess(refResponse);
        var refPayload = await refResponse.Content.ReadFromJsonAsync<GitHubRefResponse>(cancellationToken);
        var parentSha = refPayload?.Object.Sha ?? baseSha;

        var commitInfoUrl = $"https://api.github.com/repos/{owner}/{repo}/git/commits/{parentSha}";
        using var commitInfoRequest = CreateAuthorizedRequest(HttpMethod.Get, commitInfoUrl, accessToken);
        var commitInfoResponse = await _httpClient.SendAsync(commitInfoRequest, cancellationToken);
        EnsureGitHubSuccess(commitInfoResponse);
        var commitInfoJson = await commitInfoResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var baseTreeSha = commitInfoJson.GetProperty("tree").GetProperty("sha").GetString();
        if (string.IsNullOrWhiteSpace(baseTreeSha))
        {
            return null;
        }

        var treeBlobs = files
            .Select(file => new GitHubTreeBlob(file.Path.Replace('\\', '/').TrimStart('/'), "100644", "blob", file.Content))
            .ToArray();

        var treeUrl = $"https://api.github.com/repos/{owner}/{repo}/git/trees";
        using var treeRequest = CreateAuthorizedRequest(HttpMethod.Post, treeUrl, accessToken);
        treeRequest.Content = JsonContent.Create(new GitHubTreeRequest(baseTreeSha, treeBlobs));
        var treeResponse = await _httpClient.SendAsync(treeRequest, cancellationToken);
        EnsureGitHubSuccess(treeResponse);
        var treePayload = await treeResponse.Content.ReadFromJsonAsync<GitHubTreeResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(treePayload?.Sha))
        {
            return null;
        }

        var createCommitUrl = $"https://api.github.com/repos/{owner}/{repo}/git/commits";
        using var createCommitRequest = CreateAuthorizedRequest(HttpMethod.Post, createCommitUrl, accessToken);
        createCommitRequest.Content = JsonContent.Create(new GitHubCommitRequest(commitMessage, treePayload.Sha, [parentSha]));
        var createCommitResponse = await _httpClient.SendAsync(createCommitRequest, cancellationToken);
        EnsureGitHubSuccess(createCommitResponse);
        var newCommit = await createCommitResponse.Content.ReadFromJsonAsync<GitHubCommitResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(newCommit?.Sha))
        {
            return null;
        }

        using var updateRefRequest = CreateAuthorizedRequest(HttpMethod.Patch, refUrl, accessToken);
        updateRefRequest.Content = JsonContent.Create(new GitHubUpdateRefRequest(newCommit.Sha, false));
        var updateRefResponse = await _httpClient.SendAsync(updateRefRequest, cancellationToken);
        EnsureGitHubSuccess(updateRefResponse);
        return newCommit.Sha;
    }

    public async Task<GitHubPullRequestResponse?> CreatePullRequestAsync(
        string accessToken,
        string owner,
        string repo,
        string title,
        string headBranch,
        string baseBranch,
        string body,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/pulls";
        using var request = CreateAuthorizedRequest(HttpMethod.Post, url, accessToken);
        request.Content = JsonContent.Create(new GitHubPullRequestRequest(title, headBranch, baseBranch, body));
        var response = await _httpClient.SendAsync(request, cancellationToken);
        EnsureGitHubSuccess(response);
        return await response.Content.ReadFromJsonAsync<GitHubPullRequestResponse>(cancellationToken);
    }

    public async Task<bool> MergePullRequestAsync(
        string accessToken,
        string owner,
        string repo,
        int pullRequestNumber,
        string commitMessage,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/pulls/{pullRequestNumber}/merge";
        using var request = CreateAuthorizedRequest(HttpMethod.Put, url, accessToken);
        request.Content = JsonContent.Create(new GitHubMergeRequest(commitMessage));
        var response = await _httpClient.SendAsync(request, cancellationToken);
        EnsureGitHubAuthorized(response);
        return response.IsSuccessStatusCode;
    }

    public async Task<GitHubCommitInfo?> GetCommitAsync(
        string accessToken,
        string owner,
        string repo,
        string sha,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/commits/{Uri.EscapeDataString(sha)}";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureGitHubSuccess(response);
        return await response.Content.ReadFromJsonAsync<GitHubCommitInfo>(cancellationToken);
    }

    public async Task<Stream> DownloadRepositoryZipballAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/zipball/{Uri.EscapeDataString(gitRef)}";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureGitHubSuccess(response);
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async Task<long> CreateRepoWebhookAsync(
        string accessToken,
        string owner,
        string repo,
        string webhookUrl,
        string secret,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/hooks";
        using var request = CreateAuthorizedRequest(HttpMethod.Post, url, accessToken);
        request.Content = JsonContent.Create(new
        {
            name = "web",
            active = true,
            events = new[] { "push" },
            config = new
            {
                url = webhookUrl,
                content_type = "json",
                secret
            }
        });

        var response = await _httpClient.SendAsync(request, cancellationToken);
        EnsureGitHubAuthorized(response);
        response.EnsureSuccessStatusCode();
        var hook = await response.Content.ReadFromJsonAsync<GitHubHookResponse>(cancellationToken);
        if (hook is null || hook.Id <= 0)
        {
            throw new InvalidOperationException("GitHub did not return a webhook id.");
        }

        return hook.Id;
    }

    public async Task DeleteRepoWebhookAsync(
        string accessToken,
        string owner,
        string repo,
        long hookId,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/hooks/{hookId}";
        using var request = CreateAuthorizedRequest(HttpMethod.Delete, url, accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        EnsureGitHubAuthorized(response);
        response.EnsureSuccessStatusCode();
    }

    private static string NormalizeContentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or "/")
        {
            return string.Empty;
        }

        return path.Trim().Trim('/');
    }

    private static void EnsureGitHubSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new DeployAIException(
                "github_rate_limited",
                "GitHub is busy right now. Wait a moment and try again.");
        }

        if (response.StatusCode is HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout)
        {
            throw new DeployAIException(
                "github_unavailable",
                "We couldn't reach GitHub right now. Try again in a moment.");
        }

        EnsureGitHubAuthorized(response);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "github_unavailable",
                "We couldn't reach GitHub right now. Try again in a moment.");
        }
    }

    private static void EnsureGitHubAuthorized(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new DeployAIException(
                "github_auth_expired",
                "Your GitHub connection expired. Sign in again.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new DeployAIException(
                "github_access_denied",
                "DeployAI can't access this repository. Check that your GitHub account still has access.");
        }
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
