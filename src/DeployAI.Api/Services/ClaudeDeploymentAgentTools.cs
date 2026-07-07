namespace DeployAI.Api.Services;

using System.Text.Json;

internal static class ClaudeDeploymentAgentTools
{
    internal const string SubmitFilesToolName = "submit_deployment_files";
    internal const string RunLocalBuildToolName = "run_local_build";

    internal static IReadOnlyList<object> GitHubWithSubmitFiles { get; } =
    [
        .. ClaudeGitHubTools.Definitions,
        CreateSubmitFilesTool()
    ];

    internal static IReadOnlyList<object> FixAgentTools { get; } =
    [
        .. ClaudeGitHubTools.Definitions,
        CreateRunLocalBuildTool(),
        CreateSubmitFilesTool()
    ];

    internal static string DescribeToolCall(string toolName, JsonElement input) =>
        toolName switch
        {
            _ when string.Equals(toolName, SubmitFilesToolName, StringComparison.Ordinal) =>
                DescribeFilesToolCall("Submitting", input),
            _ when string.Equals(toolName, RunLocalBuildToolName, StringComparison.Ordinal) =>
                DescribeFilesToolCall("Running local build with", input),
            _ => ClaudeGitHubToolExecutor.DescribeToolCall(toolName, input)
        };

    private static object CreateSubmitFilesTool() => new
    {
        name = SubmitFilesToolName,
        description =
            "Submit the final list of repository files to create or update. " +
            "Call this exactly once after run_local_build succeeds. " +
            "Do not return file payloads as plain text or markdown.",
        input_schema = CreateFilesInputSchema()
    };

    private static object CreateRunLocalBuildTool() => new
    {
        name = RunLocalBuildToolName,
        description =
            "Run the same install/build commands as the failed deployment against draft file contents. " +
            "Call this with all modified files before submit_deployment_files. " +
            "If the build fails, read the output, fix the files, and call run_local_build again.",
        input_schema = CreateFilesInputSchema()
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
