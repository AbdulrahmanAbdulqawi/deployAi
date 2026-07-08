using System.Text;
using System.Text.Json;

namespace DeployAI.Api.Services;

internal sealed class ClaudeFixToolExecutor
{
    private readonly IFixBuildSession _buildSession;
    private readonly int _maxCommands;
    private readonly bool _allowAgentBuilds;
    private readonly Func<string, Task>? _reportActivity;

    private readonly Dictionary<string, (string Path, string Content)> _changedFiles =
        new(StringComparer.OrdinalIgnoreCase);

    private int _commandCount;
    private bool _verifiedSinceLastEdit;

    public ClaudeFixToolExecutor(
        IFixBuildSession buildSession,
        int maxCommands,
        bool allowAgentBuilds,
        Func<string, Task>? reportActivity)
    {
        _buildSession = buildSession;
        _maxCommands = Math.Max(1, maxCommands);
        _allowAgentBuilds = allowAgentBuilds;
        _reportActivity = reportActivity;
    }

    /// <summary>True once verification requirements are met for proactive submit.</summary>
    public bool CanProactiveSubmit => _allowAgentBuilds ? _verifiedSinceLastEdit : _changedFiles.Count > 0;

    /// <summary>The files Claude created or replaced with write_file, i.e. what should be committed.</summary>
    public IReadOnlyList<(string Path, string Content)> ChangedFiles =>
        _changedFiles.Values.ToArray();

    public async Task<string> ExecuteAsync(
        string toolName,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        if (string.Equals(toolName, ClaudeDeploymentAgentTools.RunCommandToolName, StringComparison.Ordinal))
        {
            return await RunCommandAsync(input, cancellationToken);
        }

        if (string.Equals(toolName, ClaudeDeploymentAgentTools.WriteFileToolName, StringComparison.Ordinal))
        {
            return WriteFile(input);
        }

        if (string.Equals(toolName, ClaudeDeploymentAgentTools.ReadFileToolName, StringComparison.Ordinal))
        {
            return ReadFile(input);
        }

        if (string.Equals(toolName, ClaudeDeploymentAgentTools.ListDirectoryToolName, StringComparison.Ordinal))
        {
            return ListDirectory(input);
        }

        return $"Error: unknown tool '{toolName}'.";
    }

    private async Task<string> RunCommandAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!_allowAgentBuilds)
        {
            return "Error: run_command is disabled. Apply fixes with write_file and call submit_deployment_files.";
        }

        if (!TryGetString(input, "command", out var command))
        {
            return "Error: 'command' is required.";
        }

        if (_commandCount >= _maxCommands)
        {
            return $"Error: command limit reached ({_maxCommands}). Fix the remaining errors with the output you already have, then submit.";
        }

        _commandCount++;
        var workingDirectory = TryGetString(input, "working_directory", out var dir) ? dir : null;

        await ReportAsync($"Running command: {command}");
        var result = await _buildSession.RunCommandAsync(command, workingDirectory, cancellationToken);

        if (result.Succeeded)
        {
            _verifiedSinceLastEdit = true;
        }

        await ReportAsync(result.Succeeded
            ? "Command succeeded."
            : "Command failed — read the output and fix remaining errors.");

        return FormatCommandResult(result);
    }

    private string WriteFile(JsonElement input)
    {
        if (!TryGetString(input, "path", out var path))
        {
            return "Error: 'path' is required.";
        }

        if (!input.TryGetProperty("content", out var contentElement) ||
            contentElement.ValueKind != JsonValueKind.String)
        {
            return "Error: 'content' is required.";
        }

        var content = contentElement.GetString() ?? string.Empty;

        try
        {
            _buildSession.WriteFile(path, content);
        }
        catch (Exception ex)
        {
            return $"Error writing '{path}': {ex.Message}";
        }

        var normalized = NormalizePath(path);
        _changedFiles[normalized] = (path, content);
        _verifiedSinceLastEdit = !_allowAgentBuilds;

        return $"Wrote {content.Length} characters to {normalized}.";
    }

    private string ReadFile(JsonElement input)
    {
        if (!TryGetString(input, "path", out var path))
        {
            return "Error: 'path' is required.";
        }

        var content = _buildSession.ReadFile(path);
        return content ?? $"Error: file '{path}' was not found in the workspace.";
    }

    private string ListDirectory(JsonElement input)
    {
        var path = TryGetString(input, "path", out var value) ? value : string.Empty;
        return _buildSession.ListDirectory(path);
    }

    private static string FormatCommandResult(FixBuildResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.Succeeded ? "COMMAND SUCCEEDED" : "COMMAND FAILED");
        builder.AppendLine($"exit_code: {result.ExitCode}");
        builder.AppendLine();
        builder.AppendLine(result.Output);
        return builder.ToString().TrimEnd();
    }

    private static bool TryGetString(JsonElement input, string property, out string value)
    {
        value = string.Empty;
        if (input.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                value = raw;
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');

    private Task ReportAsync(string message) =>
        _reportActivity is null ? Task.CompletedTask : _reportActivity(message);
}
