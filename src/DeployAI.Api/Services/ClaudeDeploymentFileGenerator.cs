using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

public sealed class ClaudeDeploymentFileGenerator : IDeploymentFileGenerator
{
    private readonly AnthropicMessageClient _anthropic;
    private readonly IGitHubService _gitHubService;

    public ClaudeDeploymentFileGenerator(
        AnthropicMessageClient anthropic,
        IGitHubService gitHubService)
    {
        _anthropic = anthropic;
        _gitHubService = gitHubService;
    }

    public async Task<IReadOnlyList<GeneratedDeploymentFile>> GenerateMissingFilesAsync(
        string owner,
        string repo,
        string gitRef,
        string githubAccessToken,
        IReadOnlyList<DeploymentPlanPart> parts,
        IReadOnlyList<MissingDeploymentFile> missingFiles,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken)
    {
        if (!_anthropic.IsConfigured)
        {
            throw new DeployAIException(
                "claude_not_configured",
                "Anthropic API key is not configured. Add Anthropic:ApiKey to generate deployment setup with Claude.");
        }

        var repoRef = new GitHubRepoRef(githubAccessToken, owner, repo, gitRef);
        var toolExecutor = new ClaudeGitHubToolExecutor(_gitHubService, _anthropic.SetupMaxToolCalls);
        var prompt = ClaudeDeploymentPrompts.BuildMissingFilesPrompt(
            owner,
            repo,
            gitRef,
            parts,
            missingFiles);

        await ReportActivityAsync(reportActivity, "Starting Claude setup agent…");
        await ReportActivityAsync(
            reportActivity,
            $"Exploring {owner}/{repo} at {gitRef} to generate {missingFiles.Count} deployment target(s).");

        var responseText = await _anthropic.RunAgentWithToolsAsync(
            prompt,
            ClaudeDeploymentAgentTools.GitHubWithSubmitFiles,
            (toolName, input, ct) => toolExecutor.ExecuteAsync(toolName, input, repoRef, ct),
            _anthropic.SetupAgentMaxTurns,
            cancellationToken,
            reportActivity,
            ClaudeDeploymentAgentTools.SubmitFilesToolName,
            _anthropic.SetupForceSubmitAfterToolCalls);

        await ReportActivityAsync(reportActivity, "Parsing Claude response…");

        var parsed = AnthropicJsonFileParser.ParseFilesResponse(
            responseText,
            GeneratedDeploymentFilePathRules.IsAllowedPath,
            "setup_generation_failed",
            "Claude returned a response that could not be parsed as JSON. Try generating the setup again.");
        if (parsed.Count == 0)
        {
            throw new DeployAIException("setup_generation_failed", "Claude returned an empty or invalid deployment setup.");
        }

        GeneratedDeploymentFileValidator.ValidateOrThrow(parsed);

        await ReportActivityAsync(reportActivity, $"Validated {parsed.Count} generated file(s).");

        return parsed
            .Select(file => new GeneratedDeploymentFile(file.Path, file.Content))
            .ToArray();
    }

    private static Task ReportActivityAsync(Func<string, Task>? reportActivity, string message) =>
        reportActivity is null ? Task.CompletedTask : reportActivity(message);
}
