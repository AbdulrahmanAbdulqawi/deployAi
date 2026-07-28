namespace DeployAI.Api.Services;

using System.Text.Json;

/// <summary>Defines the Anthropic tool-use tool schemas (submit files, run command, read/write file, memory) offered to Claude for deployment setup and fix-generation agent runs.</summary>
internal static class ClaudeDeploymentAgentTools
{
    internal const string SubmitFilesToolName = "submit_deployment_files";
    internal const string RunCommandToolName = "run_command";
    internal const string WriteFileToolName = "write_file";
    internal const string ReadFileToolName = "read_file";
    internal const string ListDirectoryToolName = "list_directory";
    internal const string MemoryToolName = "memory";

    internal static IReadOnlyList<object> GitHubWithSubmitFiles { get; } =
    [
        .. ClaudeGitHubTools.Definitions,
        CreateSubmitFilesTool()
    ];

    internal static IReadOnlyList<object> FixAgentTools(bool allowAgentBuilds, bool includeMemory = true)
    {
        List<object> tools = allowAgentBuilds
            ?
            [
                CreateReadFileTool(),
                CreateListDirectoryTool(),
                CreateWriteFileTool(),
                CreateRunCommandTool(),
                CreateSubmitFilesTool()
            ]
            :
            [
                CreateReadFileTool(),
                CreateListDirectoryTool(),
                CreateWriteFileTool(),
                CreateSubmitFilesTool(allowAgentBuilds: false)
            ];

        if (includeMemory)
        {
            tools.Add(CreateMemoryTool());
        }

        return tools;
    }

    private static object CreateSubmitFilesTool(bool allowAgentBuilds = true) => new
    {
        name = SubmitFilesToolName,
        description = allowAgentBuilds
            ? "Submit the final list of repository files to create or update. " +
              "Call this exactly once after the verification build succeeds. " +
              "Include every file you changed via write_file, with full contents. " +
              "Do not return file payloads as plain text or markdown."
            : "Submit the final list of repository files to create or update. " +
              "Call this exactly once after all fixes are written with write_file. " +
              "Local build verification is disabled — do not attempt to run builds before submitting. " +
              "Include every file you changed via write_file, with full contents. " +
              "Do not return file payloads as plain text or markdown.",
        input_schema = CreateFilesInputSchema()
    };

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
            _ when string.Equals(toolName, MemoryToolName, StringComparison.Ordinal) =>
                $"Memory: {DescribeStringProperty(input, "command", "?")} {DescribeStringProperty(input, "path", string.Empty)}",
            _ => ClaudeGitHubToolExecutor.DescribeToolCall(toolName, input)
        };

    // Anthropic-defined, schema-less tool — declare only type/name, no input_schema.
    // See: https://platform.claude.com/docs/en/agents-and-tools/tool-use/memory-tool
    private static object CreateMemoryTool() => new
    {
        type = "memory_20250818",
        name = MemoryToolName
    };

    private static object CreateRunCommandTool() => new
    {
        name = RunCommandToolName,
        description =
            "Run a single shell command in the workspace (for example: 'npm install', 'npm run build', " +
            "'dotnet build'). Returns the exit code and combined stdout/stderr. " +
            "For .NET fixes, use dotnet build only — not dotnet publish or -c Release. " +
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
