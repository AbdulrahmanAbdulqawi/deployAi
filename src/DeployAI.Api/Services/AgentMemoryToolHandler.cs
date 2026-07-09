using System.Text.Json;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>
/// Storage backend for Anthropic's memory tool (`memory_20250818`). Claude decides what's
/// worth remembering and issues view/create/str_replace/insert/delete/rename commands against
/// a `/memories` directory; this class persists that directory as rows scoped to one project.
/// </summary>
internal sealed class AgentMemoryToolHandler
{
    private const string MemoryRoot = "/memories";

    private readonly DeployAIDbContext _db;
    private readonly Guid _projectId;

    public AgentMemoryToolHandler(DeployAIDbContext db, Guid projectId)
    {
        _db = db;
        _projectId = projectId;
    }

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var command = GetString(input, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return "Error: 'command' is required.";
        }

        return command switch
        {
            "view" => await ViewAsync(input, cancellationToken),
            "create" => await CreateAsync(input, cancellationToken),
            "str_replace" => await StrReplaceAsync(input, cancellationToken),
            "insert" => await InsertAsync(input, cancellationToken),
            "delete" => await DeleteAsync(input, cancellationToken),
            "rename" => await RenameAsync(input, cancellationToken),
            _ => $"Error: unknown memory command '{command}'."
        };
    }

    /// <summary>
    /// Appends a dated line to a fixed history file, creating it if absent. Used by the
    /// automatic-ingestion hooks (deploy failure recorded, fix generated) so memory
    /// accumulates even when nobody asks Claude to write it itself.
    /// </summary>
    public async Task AppendHistoryAsync(string note, CancellationToken cancellationToken)
    {
        const string path = "history.md";
        var line = $"- {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC — {note}";

        var file = await _db.AgentMemoryFiles
            .FirstOrDefaultAsync(f => f.ProjectId == _projectId && f.Path == path, cancellationToken);

        if (file is null)
        {
            _db.AgentMemoryFiles.Add(new AgentMemoryFile
            {
                Id = Guid.NewGuid(),
                ProjectId = _projectId,
                Path = path,
                Content = line + "\n",
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            file.Content += line + "\n";
            file.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> ViewAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var rawPath = GetString(input, "path");
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath == MemoryRoot || rawPath == $"{MemoryRoot}/")
        {
            var files = await _db.AgentMemoryFiles
                .Where(f => f.ProjectId == _projectId)
                .OrderBy(f => f.Path)
                .Select(f => f.Path)
                .ToListAsync(cancellationToken);

            return files.Count == 0
                ? $"{MemoryRoot} is empty."
                : string.Join("\n", files.Select(p => $"{MemoryRoot}/{p}"));
        }

        if (!TryNormalizePath(rawPath, out var path, out var error))
        {
            return error;
        }

        var file = await GetFileAsync(path, cancellationToken);
        return file is null
            ? $"Error: file '{MemoryRoot}/{path}' was not found."
            : file.Content;
    }

    private async Task<string> CreateAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryNormalizePath(GetString(input, "path"), out var path, out var error))
        {
            return error;
        }

        var content = GetString(input, "file_text") ?? string.Empty;
        var file = await GetFileAsync(path, cancellationToken);
        if (file is null)
        {
            _db.AgentMemoryFiles.Add(new AgentMemoryFile
            {
                Id = Guid.NewGuid(),
                ProjectId = _projectId,
                Path = path,
                Content = content,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            file.Content = content;
            file.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return $"Wrote {MemoryRoot}/{path}.";
    }

    private async Task<string> StrReplaceAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryNormalizePath(GetString(input, "path"), out var path, out var error))
        {
            return error;
        }

        var file = await GetFileAsync(path, cancellationToken);
        if (file is null)
        {
            return $"Error: file '{MemoryRoot}/{path}' was not found.";
        }

        var oldStr = GetString(input, "old_str") ?? string.Empty;
        var newStr = GetString(input, "new_str") ?? string.Empty;

        var occurrences = CountOccurrences(file.Content, oldStr);
        if (occurrences == 0)
        {
            return $"Error: 'old_str' was not found in {MemoryRoot}/{path}.";
        }

        if (occurrences > 1)
        {
            return $"Error: 'old_str' matches {occurrences} locations in {MemoryRoot}/{path}; it must match exactly one.";
        }

        file.Content = file.Content.Replace(oldStr, newStr, StringComparison.Ordinal);
        file.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return $"Updated {MemoryRoot}/{path}.";
    }

    private async Task<string> InsertAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryNormalizePath(GetString(input, "path"), out var path, out var error))
        {
            return error;
        }

        var file = await GetFileAsync(path, cancellationToken);
        if (file is null)
        {
            return $"Error: file '{MemoryRoot}/{path}' was not found.";
        }

        var insertLine = GetInt(input, "insert_line") ?? 0;
        var insertText = GetString(input, "insert_text") ?? string.Empty;

        var lines = file.Content.Split('\n').ToList();
        var index = Math.Clamp(insertLine, 0, lines.Count);
        lines.Insert(index, insertText);

        file.Content = string.Join('\n', lines);
        file.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return $"Inserted into {MemoryRoot}/{path} at line {index}.";
    }

    private async Task<string> DeleteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryNormalizePath(GetString(input, "path"), out var path, out var error))
        {
            return error;
        }

        var file = await GetFileAsync(path, cancellationToken);
        if (file is null)
        {
            return $"Error: file '{MemoryRoot}/{path}' was not found.";
        }

        _db.AgentMemoryFiles.Remove(file);
        await _db.SaveChangesAsync(cancellationToken);
        return $"Deleted {MemoryRoot}/{path}.";
    }

    private async Task<string> RenameAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryNormalizePath(GetString(input, "old_path") ?? GetString(input, "path"), out var oldPath, out var error))
        {
            return error;
        }

        if (!TryNormalizePath(GetString(input, "new_path"), out var newPath, out var newPathError))
        {
            return newPathError;
        }

        var file = await GetFileAsync(oldPath, cancellationToken);
        if (file is null)
        {
            return $"Error: file '{MemoryRoot}/{oldPath}' was not found.";
        }

        if (await GetFileAsync(newPath, cancellationToken) is not null)
        {
            return $"Error: '{MemoryRoot}/{newPath}' already exists.";
        }

        file.Path = newPath;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return $"Renamed {MemoryRoot}/{oldPath} to {MemoryRoot}/{newPath}.";
    }

    private Task<AgentMemoryFile?> GetFileAsync(string path, CancellationToken cancellationToken) =>
        _db.AgentMemoryFiles.FirstOrDefaultAsync(f => f.ProjectId == _projectId && f.Path == path, cancellationToken);

    /// <summary>
    /// Strips the fixed `/memories` mount prefix and rejects traversal-looking paths. Rows are
    /// already scoped by ProjectId, but a malformed path must not be allowed to collide with an
    /// unrelated logical file, and memory files must never be able to reference anything
    /// resembling a filesystem path outside the mount.
    /// </summary>
    private static bool TryNormalizePath(string? rawPath, out string path, out string error)
    {
        path = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "Error: 'path' is required.";
            return false;
        }

        var trimmed = rawPath.Trim();
        if (trimmed.StartsWith(MemoryRoot, StringComparison.Ordinal))
        {
            trimmed = trimmed[MemoryRoot.Length..];
        }

        trimmed = trimmed.Trim('/');

        if (trimmed.Length == 0 ||
            trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.Contains('\\'))
        {
            error = $"Error: invalid memory path '{rawPath}'.";
            return false;
        }

        path = trimmed;
        return true;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string? GetString(JsonElement input, string property) =>
        input.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? GetInt(JsonElement input, string property) =>
        input.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetInt32()
            : null;
}
