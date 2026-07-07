namespace DeployAI.Api.Services;

using System.Text.Json;

internal static class ClaudeDeploymentAgentTools
{
    internal const string SubmitFilesToolName = "submit_deployment_files";
    internal const string RunCommandToolName = "run_command";
    internal const string WriteFileToolName = "write_file";
    internal const string ReadFileToolName = "read_file";
    internal const string ListDirectoryToolName = "list_directory";

    internal static IReadOnlyList<object> GitHubWithSubmitFiles { get; } =
    [
        .. ClaudeGitHubTools.Definitions,
        CreateSubmitFilesTool()
    ];

    internal static IReadOnlyList<object> FixAgentTools { get; } =
    [
        CreateReadFileTool(),
        CreateListDirectoryTool(),
        CreateWriteFileTool(),
        CreateRunCommandTool(),
        CreateSubmitFilesTool()
    ];

    internal static string DescribeToolCall(string toolName, JsonElement input) =>
        toolName switch
        {
            _ when string.Equals(toolName, SubmitFilesToolName, StringComparison.Ordinal) =>
                DescribeFilesToolCall("Submitting", input),
            _ when string.Equals(toolName, RunCommandToolName, StringComparison.Ordinal) =>
                $"Running command: {DescribeStringProperty(input, "command", "(command missing)")}",
            _ when string.Equals(toolName, WriteFileToolName, StringComparison.Ordinal) =>
                $"Writing file: {DescribeStringProperty(input, "path", "(path missing)")}",
            _ when string.Equals(toolName, ReadFileToolName, StringComparison.Ordinal) =>
                $"Reading file: {DescribeStringProperty(input, "path", "(path missing)")}",
            _ when string.Equals(toolName, ListDirectoryToolName, StringComparison.Ordinal) =>
                $"Listing directory: {DescribeStringProperty(input, "path", "/")}",
            _ => ClaudeGitHubToolExecutor.DescribeToolCall(toolName, input)
        };

    private static object CreateSubmitFilesTool() => new
    {
        name = SubmitFilesToolName,
        description =
            "Submit the final list of repository files to create or update. " +
            "Call this exactly once after your build command succeeds. " +
            "Include every file you changed via write_file, with full contents. " +
            "Do not return file payloads as plain text or markdown.",
        input_schema = CreateFilesInputSchema()
    };

    private static object CreateRunCommandTool() => new
    {
        name = RunCommandToolName,
        description =
            "Run a single shell command in the workspace (for example: 'npm install', 'npm run build', " +
            "'dotnet build'). Returns the exit code and combined stdout/stderr. " +
            "The workspace persists between commands, so installed dependencies are reused. " +
            "There is no network allow-list beyond package registries; do not attempt other network access.",
        input_schema = new
        {
            type = "object",
            properties = new
            {
                command = new
                {
                    type = "string",
                    description = "The shell command to execute."
                },
                working_directory = new
                {
                    type = "string",
                    description = "Optional subdirectory (relative to the repo root) to run the command in."
                }
            },
            required = new[] { "command" },
            additionalProperties = false
        }
    };

    private static object CreateWriteFileTool() => new
    {
        name = WriteFileToolName,
        description =
            "Create or replace a file in the workspace with full contents (not a diff). " +
            "Use this to apply your fixes before running build commands. " +
            "Only files written with this tool are included in the final commit.",
        input_schema = new
        {
            type = "object",
            properties = new
            {
                path = new
                {
                    type = "string",
                    description = "Relative path from the repository root."
                },
                content = new
                {
                    type = "string",
                    description = "Complete file contents (not a diff)."
                }
            },
            required = new[] { "path", "content" },
            additionalProperties = false
        }
    };

    private static object CreateReadFileTool() => new
    {
        name = ReadFileToolName,
        description =
            "Read the current text contents of a file in the workspace, reflecting any edits you have " +
            "already made with write_file. Use this to inspect source and config before changing it.",
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
            required = new[] { "path" },
            additionalProperties = false
        }
    };

    private static object CreateListDirectoryTool() => new
    {
        name = ListDirectoryToolName,
        description =
            "List files and folders at a path in the workspace. " +
            "Use an empty path or omit it to list the repository root.",
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
            },
            additionalProperties = false
        }
    };

    private static object CreateFilesInputSchema() => new
    {
        type = "object",
        properties = new
        {
            files = new
            {
                type = "array",
                description = "Files to create or replace, with full contents for each path.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new
                        {
                            type = "string",
                            description = "Relative path from the repository root."
                        },
                        content = new
                        {
                            type = "string",
                            description = "Complete file contents (not a diff)."
                        }
                    },
                    required = new[] { "path", "content" },
                    additionalProperties = false
                }
            }
        },
        required = new[] { "files" },
        additionalProperties = false
    };

    private static string DescribeStringProperty(JsonElement input, string property, string fallback)
    {
        if (input.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string DescribeFilesToolCall(string prefix, JsonElement input)
    {
        if (input.TryGetProperty("files", out var filesElement) &&
            filesElement.ValueKind == JsonValueKind.Array)
        {
            var count = filesElement.GetArrayLength();
            return count == 1
                ? $"{prefix} 1 file…"
                : $"{prefix} {count} files…";
        }

        return $"{prefix} files…";
    }
}
