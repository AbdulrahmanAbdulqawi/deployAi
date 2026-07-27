using System.Text;
using System.Text.Json;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

/// <summary>Identifies the GitHub repo/ref a Claude agent run's tool calls should read from.</summary>
internal readonly record struct GitHubRepoRef(
    string AccessToken,
    string Owner,
    string Repo,
    string GitRef);

/// <summary>Defines the read-only GitHub browsing tool schemas (list directory, read file) offered to Claude for deployment setup generation.</summary>
internal static class ClaudeGitHubTools
{
    internal static IReadOnlyList<object> Definitions { get; } =
    [
        new
        {
            name = "github_list_directory",
            description =
                "List files and folders at a path in the connected GitHub repository. " +
                "Use an empty path or omit it to list the repository root. " +
                "Use this to explore the project structure before reading files.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    path = new
                    {
                        type = "string",
                        description = "Relative directory path from the repository root. Use \"\" for root."
                    }
                }
            }
        },
        new
        {
            name = "github_read_file",
            description =
                "Read the text content of a file from the connected GitHub repository at the deployment git ref. " +
                "Use this to inspect source code, configuration, and build files related to the failure.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    path = new
                    {
                        type = "string",
                        description = "Relative file path from the repository root."
                    }
                },
                required = new[] { "path" }
            }
        }
    ];
}

/// <summary>Executes the GitHub browsing tool calls Claude makes during a deployment-setup agent run, truncating large files/listings to keep them within prompt size limits.</summary>
internal sealed class ClaudeGitHubToolExecutor
{
    private const int DefaultMaxFileChars = 120_000;
    private const int DefaultMaxListEntries = 200;

    private readonly IGitHubService _gitHubService;
    private readonly int _maxToolCalls;
    private readonly int _maxFileChars;
    private readonly int _maxListEntries;
    private int _toolCallCount;

    public ClaudeGitHubToolExecutor(
        IGitHubService gitHubService,
        int maxToolCalls = 80,
        int maxFileChars = DefaultMaxFileChars,
        int maxListEntries = DefaultMaxListEntries)
    {
        _gitHubService = gitHubService;
        _maxToolCalls = maxToolCalls;
        _maxFileChars = maxFileChars;
        _maxListEntries = maxListEntries;
    }

    public async Task<string> ExecuteAsync(
        string toolName,
        JsonElement input,
        GitHubRepoRef repo,
        CancellationToken cancellationToken)
    {
        if (++_toolCallCount > _maxToolCalls)
        {
            return "Error: repository tool call limit reached. Propose a fix using the files you already inspected.";
        }

        return toolName switch
        {
            "github_list_directory" => await ListDirectoryAsync(input, repo, cancellationToken),
            "github_read_file" => await ReadFileAsync(input, repo, cancellationToken),
            _ => $"Error: unknown tool '{toolName}'."
        };
    }

    internal static string DescribeToolCall(string toolName, JsonElement input) =>
        toolName switch
        {
            "github_list_directory" =>
                $"Listing directory: {DescribeDirectoryPath(input)}",
            "github_read_file" when input.TryGetProperty("path", out var pathElement) &&
                                    !string.IsNullOrWhiteSpace(pathElement.GetString()) =>
                $"Reading file: {pathElement.GetString()}",
            "github_read_file" => "Reading file: (path missing)",
            _ => $"Running tool: {toolName}"
        };

    private static string DescribeDirectoryPath(JsonElement input)
    {
        if (!input.TryGetProperty("path", out var pathElement))
        {
            return "/";
        }

        var path = pathElement.GetString()?.Trim().TrimStart('/');
        return string.IsNullOrWhiteSpace(path) ? "/" : path;
    }

    private async Task<string> ListDirectoryAsync(
        JsonElement input,
        GitHubRepoRef repo,
        CancellationToken cancellationToken)
    {
        var path = input.TryGetProperty("path", out var pathElement)
            ? pathElement.GetString() ?? string.Empty
            : string.Empty;

        if (!TryNormalizeRepoPath(path, out var normalizedPath, out var pathError))
        {
            return pathError!;
        }

        IReadOnlyList<GitHubContentItem> entries;
        try
        {
            entries = await _gitHubService.ListContentsAsync(
                repo.AccessToken,
                repo.Owner,
                repo.Repo,
                string.IsNullOrEmpty(normalizedPath) ? null : normalizedPath,
                repo.GitRef,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error listing '{normalizedPath}': {ex.Message}";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Directory: {(string.IsNullOrEmpty(normalizedPath) ? "/" : normalizedPath)}");
        builder.AppendLine($"Entries: {Math.Min(entries.Count, _maxListEntries)}");

        foreach (var entry in entries.Take(_maxListEntries))
        {
            builder.AppendLine($"- [{entry.Type}] {entry.Path}");
        }

        if (entries.Count > _maxListEntries)
        {
            builder.AppendLine($"... truncated {entries.Count - _maxListEntries} additional entries");
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> ReadFileAsync(
        JsonElement input,
        GitHubRepoRef repo,
        CancellationToken cancellationToken)
    {
        if (!input.TryGetProperty("path", out var pathElement) ||
            string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            return "Error: 'path' is required.";
        }

        if (!TryNormalizeRepoPath(pathElement.GetString()!, out var normalizedPath, out var pathError))
        {
            return pathError!;
        }

        string? content;
        try
        {
            content = await _gitHubService.GetFileContentAsync(
                repo.AccessToken,
                repo.Owner,
                repo.Repo,
                normalizedPath,
                repo.GitRef,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error reading '{normalizedPath}': {ex.Message}";
        }

        if (content is null)
        {
            return $"Error: file '{normalizedPath}' was not found at ref {repo.GitRef}.";
        }

        if (content.Contains('\0', StringComparison.Ordinal))
        {
            return $"Error: '{normalizedPath}' appears to be a binary file and cannot be read as text.";
        }

        if (content.Length > _maxFileChars)
        {
            return content[.._maxFileChars] +
                   $"\n\n... truncated ({content.Length - _maxFileChars} additional characters not shown)";
        }

        return content;
    }

    private static bool TryNormalizeRepoPath(string path, out string normalizedPath, out string? error)
    {
        normalizedPath = path.Replace('\\', '/').Trim().TrimStart('/');
        if (normalizedPath.Contains("..", StringComparison.Ordinal))
        {
            normalizedPath = string.Empty;
            error = "Error: path traversal is not allowed.";
            return false;
        }

        error = null;
        return true;
    }
}
