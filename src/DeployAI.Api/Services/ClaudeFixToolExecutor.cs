using System.Text;
using System.Text.Json;
using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

internal sealed class ClaudeFixToolExecutor
{
    private readonly ClaudeGitHubToolExecutor _gitHubExecutor;
    private readonly IFixBuildWorkspaceService _buildWorkspace;
    private readonly string _accessToken;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _gitRef;
    private readonly string _providerName;
    private readonly string? _framework;
    private readonly DeployTargetConfig _targetConfig;
    private readonly DeploymentFailureAnalysis _failureAnalysis;
    private readonly Func<string, Task>? _reportActivity;

    private bool _buildSucceeded;

    public ClaudeFixToolExecutor(
        IGitHubService gitHubService,
        IFixBuildWorkspaceService buildWorkspace,
        int maxGitHubToolCalls,
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        string providerName,
        string? framework,
        DeployTargetConfig targetConfig,
        DeploymentFailureAnalysis failureAnalysis,
        Func<string, Task>? reportActivity)
    {
        _gitHubExecutor = new ClaudeGitHubToolExecutor(gitHubService, maxGitHubToolCalls);
        _buildWorkspace = buildWorkspace;
        _accessToken = accessToken;
        _owner = owner;
        _repo = repo;
        _gitRef = gitRef;
        _providerName = providerName;
        _framework = framework;
        _targetConfig = targetConfig;
        _failureAnalysis = failureAnalysis;
        _reportActivity = reportActivity;
    }

    public bool BuildSucceeded => _buildSucceeded;

    public bool CanProactiveSubmit => _buildSucceeded;

    public async Task<string> ExecuteAsync(
        string toolName,
        JsonElement input,
        GitHubRepoRef repo,
        CancellationToken cancellationToken)
    {
        if (string.Equals(toolName, ClaudeDeploymentAgentTools.RunLocalBuildToolName, StringComparison.Ordinal))
        {
            var files = AnthropicJsonFileParser.ParseFilesFromToolInput(input);
            if (files.Count == 0)
            {
                return "Error: run_local_build requires at least one file in the files array.";
            }

            await ReportAsync("Running local build to verify the proposed fix…");
            var result = await _buildWorkspace.RunBuildAsync(
                _accessToken,
                _owner,
                _repo,
                _gitRef,
                _providerName,
                _targetConfig,
                _framework,
                _failureAnalysis,
                files,
                cancellationToken);

            _buildSucceeded = result.Succeeded;
            await ReportAsync(result.Succeeded
                ? "Local build succeeded."
                : "Local build failed — read the output and fix remaining errors.");

            return FormatBuildResult(result);
        }

        return await _gitHubExecutor.ExecuteAsync(toolName, input, repo, cancellationToken);
    }

    private static string FormatBuildResult(FixBuildResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.Succeeded ? "BUILD SUCCEEDED" : "BUILD FAILED");
        builder.AppendLine($"exit_code: {result.ExitCode}");
        builder.AppendLine();
        builder.AppendLine(result.Output);
        return builder.ToString().TrimEnd();
    }

    private Task ReportAsync(string message) =>
        _reportActivity is null ? Task.CompletedTask : _reportActivity(message);
}
