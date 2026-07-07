using System.Text;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

public sealed class ClaudeDeploymentFixGenerator : IDeploymentFixGenerator
{
    private const int MaxPreloadedFiles = 12;
    private const int MaxPreloadedFileChars = 8_000;

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

        await ReportAsync(reportActivity, "Preparing local build workspace…");
        await using var buildSession = await _buildWorkspace.CreateSessionAsync(
            githubAccessToken,
            owner,
            repo,
            gitRef,
            providerName,
            targetConfig,
            framework,
            failureAnalysis,
            cancellationToken);

        var toolExecutor = new ClaudeFixToolExecutor(
            buildSession,
            _anthropic.FixMaxCommands,
            reportActivity);

        var repoContext = await BuildRepoContextAsync(repoRef, targetConfig, failureAnalysis, cancellationToken);

        var prompt = ClaudeDeploymentPrompts.BuildFixPrompt(
            owner,
            repo,
            gitRef,
            providerName,
            framework,
            failureAnalysis,
            repoContext);

        await ReportAsync(reportActivity, "Starting Claude fix agent…");

        var responseText = await _anthropic.RunAgentWithToolsAsync(
            prompt,
            ClaudeDeploymentAgentTools.FixAgentTools,
            (toolName, input, ct) => toolExecutor.ExecuteAsync(toolName, input, ct),
            _anthropic.FixAgentMaxTurns,
            cancellationToken,
            reportActivity,
            ClaudeDeploymentAgentTools.SubmitFilesToolName,
            _anthropic.FixForceSubmitAfterToolCalls,
            () => toolExecutor.CanProactiveSubmit);

        await ReportAsync(reportActivity, "Parsing Claude fix files…");

        var changedFiles = ResolveChangedFiles(toolExecutor, responseText);
        if (changedFiles.Count == 0)
        {
            throw new DeployAIException("fix_generation_failed", "Claude returned an empty or invalid fix.");
        }

        GeneratedDeploymentFileValidator.ValidateOrThrow(changedFiles);

        if (buildSession.IsEnabled)
        {
            await ReportAsync(reportActivity, "Running final local build verification…");
            var gateResult = await buildSession.RunBuildAsync(changedFiles, cancellationToken);

            if (!gateResult.Succeeded)
            {
                throw new DeployAIException(
                    "fix_generation_failed",
                    $"Claude's fix did not pass a local build verification. Build output:{Environment.NewLine}{gateResult.Output}");
            }

            await ReportAsync(reportActivity, $"Build verification passed ({changedFiles.Count} file(s)).");
        }

        return changedFiles
            .Select(file => new GeneratedDeploymentFile(file.Path, file.Content))
            .ToArray();
    }

    /// <summary>
    /// The authoritative set of files to commit is what Claude wrote via write_file. As a fallback
    /// (e.g. the agent submitted files without using write_file), parse the submission payload.
    /// </summary>
    private static IReadOnlyList<(string Path, string Content)> ResolveChangedFiles(
        ClaudeFixToolExecutor toolExecutor,
        string responseText)
    {
        var changed = toolExecutor.ChangedFiles
            .Where(file => GeneratedDeploymentFilePathRules.IsAllowedPath(file.Path))
            .ToArray();
        if (changed.Length > 0)
        {
            return changed;
        }

        return AnthropicJsonFileParser.ParseFilesResponse(
            responseText,
            GeneratedDeploymentFilePathRules.IsAllowedPath);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private async Task<string?> BuildRepoContextAsync(
        GitHubRepoRef repoRef,
        DeployTargetConfig targetConfig,
        DeploymentFailureAnalysis failureAnalysis,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();

        var treeListing = await TryListRootAsync(repoRef, targetConfig, cancellationToken);
        if (!string.IsNullOrWhiteSpace(treeListing))
        {
            builder.AppendLine("### Repository layout (root)");
            builder.AppendLine("```");
            builder.AppendLine(treeListing.TrimEnd());
            builder.AppendLine("```");
            builder.AppendLine();
        }

        var referencedFiles = failureAnalysis.ReferencedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPreloadedFiles)
            .ToArray();

        if (referencedFiles.Length > 0)
        {
            var contents = await Task.WhenAll(referencedFiles.Select(path =>
                TryReadFileAsync(repoRef, path, cancellationToken)));

            var appendedAny = false;
            for (var i = 0; i < referencedFiles.Length; i++)
            {
                var content = contents[i];
                if (content is null)
                {
                    continue;
                }

                if (!appendedAny)
                {
                    builder.AppendLine("### Preloaded files referenced by the errors");
                    builder.AppendLine(
                        "These files are already included below — do NOT call github_read_file for them again unless you need a newer version.");
                    builder.AppendLine();
                    appendedAny = true;
                }

                builder.AppendLine($"#### {referencedFiles[i]}");
                builder.AppendLine("```");
                builder.AppendLine(content.TrimEnd());
                builder.AppendLine("```");
                builder.AppendLine();
            }
        }

        return builder.Length == 0 ? null : builder.ToString().TrimEnd();
    }

    private async Task<string?> TryListRootAsync(
        GitHubRepoRef repoRef,
        DeployTargetConfig targetConfig,
        CancellationToken cancellationToken)
    {
        try
        {
            var rootDirectory = targetConfig.RootDirectory ?? targetConfig.ServiceDirectory;
            var normalizedRoot = string.IsNullOrWhiteSpace(rootDirectory)
                ? null
                : rootDirectory.Trim().Trim('/');

            var entries = await _gitHubService.ListContentsAsync(
                repoRef.AccessToken,
                repoRef.Owner,
                repoRef.Repo,
                normalizedRoot,
                repoRef.GitRef,
                cancellationToken);

            if (entries.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var entry in entries.Take(100))
            {
                builder.AppendLine($"- [{entry.Type}] {entry.Path}");
            }

            return builder.ToString();
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryReadFileAsync(
        GitHubRepoRef repoRef,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await _gitHubService.GetFileContentAsync(
                repoRef.AccessToken,
                repoRef.Owner,
                repoRef.Repo,
                path,
                repoRef.GitRef,
                cancellationToken);

            if (string.IsNullOrEmpty(content) || content.Contains('\0', StringComparison.Ordinal))
            {
                return null;
            }

            return content.Length > MaxPreloadedFileChars
                ? content[..MaxPreloadedFileChars] +
                  $"\n\n... truncated ({content.Length - MaxPreloadedFileChars} additional characters not shown)"
                : content;
        }
        catch
        {
            return null;
        }
    }

    private static Task ReportAsync(Func<string, Task>? reportActivity, string message) =>
        reportActivity is null ? Task.CompletedTask : reportActivity(message);
}
