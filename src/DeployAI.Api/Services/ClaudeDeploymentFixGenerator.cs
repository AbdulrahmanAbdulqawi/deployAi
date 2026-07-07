using DeployAI.Core.Deployments;

using DeployAI.Core.Exceptions;

using DeployAI.Infrastructure.GitHub;



namespace DeployAI.Api.Services;



public sealed class ClaudeDeploymentFixGenerator : IDeploymentFixGenerator

{

    private readonly AnthropicMessageClient _anthropic;

    private readonly IGitHubService _gitHubService;

    private readonly IFixBuildWorkspaceService _buildWorkspace;



    public ClaudeDeploymentFixGenerator(

        AnthropicMessageClient anthropic,

        IGitHubService gitHubService,

        IFixBuildWorkspaceService buildWorkspace)

    {

        _anthropic = anthropic;

        _gitHubService = gitHubService;

        _buildWorkspace = buildWorkspace;

    }



    public async Task<IReadOnlyList<GeneratedDeploymentFile>> GenerateFixFilesAsync(

        string owner,

        string repo,

        string gitRef,

        string githubAccessToken,

        string providerName,

        string? framework,

        DeployTargetConfig targetConfig,

        DeploymentFailureAnalysis failureAnalysis,

        Func<string, Task>? reportActivity,

        CancellationToken cancellationToken)

    {

        if (!_anthropic.IsConfigured)

        {

            throw new DeployAIException(

                "claude_not_configured",

                "Anthropic API key is not configured. Add Anthropic:ApiKey to fix build errors with Claude.");

        }



        var repoRef = new GitHubRepoRef(githubAccessToken, owner, repo, gitRef);

        var toolExecutor = new ClaudeFixToolExecutor(

            _gitHubService,

            _buildWorkspace,

            _anthropic.FixMaxToolCalls,

            githubAccessToken,

            owner,

            repo,

            gitRef,

            providerName,

            framework,

            targetConfig,

            failureAnalysis,

            reportActivity);



        var prompt = ClaudeDeploymentPrompts.BuildFixPrompt(

            owner,

            repo,

            gitRef,

            providerName,

            framework,

            failureAnalysis);



        await ReportAsync(reportActivity, "Starting Claude fix agent…");



        var responseText = await _anthropic.RunAgentWithToolsAsync(

            prompt,

            ClaudeDeploymentAgentTools.FixAgentTools,

            (toolName, input, ct) => toolExecutor.ExecuteAsync(toolName, input, repoRef, ct),

            _anthropic.FixAgentMaxTurns,

            cancellationToken,

            reportActivity,

            ClaudeDeploymentAgentTools.SubmitFilesToolName,

            _anthropic.FixForceSubmitAfterToolCalls,

            () => toolExecutor.CanProactiveSubmit);



        await ReportAsync(reportActivity, "Parsing Claude fix files…");



        var parsed = AnthropicJsonFileParser.ParseFilesResponse(

            responseText,

            GeneratedDeploymentFilePathRules.IsAllowedPath);

        if (parsed.Count == 0)

        {

            throw new DeployAIException("fix_generation_failed", "Claude returned an empty or invalid fix.");

        }



        GeneratedDeploymentFileValidator.ValidateOrThrow(parsed);



        await ReportAsync(reportActivity, "Running final local build verification…");

        var gateResult = await _buildWorkspace.RunBuildAsync(

            githubAccessToken,

            owner,

            repo,

            gitRef,

            providerName,

            targetConfig,

            framework,

            failureAnalysis,

            parsed,

            cancellationToken);



        if (!gateResult.Succeeded)

        {

            throw new DeployAIException(

                "fix_generation_failed",

                $"Claude's fix did not pass a local build verification. Build output:{Environment.NewLine}{gateResult.Output}");

        }



        await ReportAsync(reportActivity, $"Build verification passed ({parsed.Count} file(s)).");



        return parsed

            .Select(file => new GeneratedDeploymentFile(file.Path, file.Content))

            .ToArray();

    }



    private static Task ReportAsync(Func<string, Task>? reportActivity, string message) =>

        reportActivity is null ? Task.CompletedTask : reportActivity(message);

}


