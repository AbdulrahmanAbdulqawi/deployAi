using DeployAI.Api.Services.DeploymentTemplates;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

/// <summary>Generates missing split-origin setup files by scaffolding template-based ones directly and using Claude only for the files that need repo-specific content.</summary>
public sealed class HybridDeploymentFileGenerator : IDeploymentFileGenerator
{
    private readonly AnthropicMessageClient _anthropic;
    private readonly IGitHubService _gitHubService;
    private readonly DeploymentFileScaffolder _scaffolder;
    private readonly DeploymentSetupFileFetcher _fileFetcher;
    private readonly DeploymentTemplateResolver _templateResolver;

    public HybridDeploymentFileGenerator(
        AnthropicMessageClient anthropic,
        IGitHubService gitHubService,
        DeploymentFileScaffolder scaffolder,
        DeploymentSetupFileFetcher fileFetcher,
        DeploymentTemplateResolver templateResolver)
    {
        _anthropic = anthropic;
        _gitHubService = gitHubService;
        _scaffolder = scaffolder;
        _fileFetcher = fileFetcher;
        _templateResolver = templateResolver;
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

        await ReportActivityAsync(reportActivity, "Loading repository files for hybrid template generation…");
        var existingFiles = await _fileFetcher.FetchGapFilesAsync(
            githubAccessToken,
            owner,
            repo,
            gitRef,
            parts,
            missingFiles,
            cancellationToken);

        var resolvedTemplates = _templateResolver.ResolveForGaps(parts, missingFiles, existingFiles);
        var deterministicFiles = _scaffolder.ScaffoldMissingFiles(parts, missingFiles, existingFiles);
        var deterministicPaths = deterministicFiles
            .Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A deterministic patch that couldn't be applied for real leaves a "// TODO:" marker
        // instead of returning null (see DeploymentFileScaffolder.PatchSignalRService /
        // PatchCookieOptionsForCrossOrigin). Those paths still count as "not actually solved",
        // so forward them to Claude too instead of letting the TODO stub ship as-is.
        var unresolvedDeterministicPaths = deterministicFiles
            .Where(file => file.Content.Contains("// TODO:", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var patchGaps = missingFiles
            .Where(missing => !deterministicPaths.Contains(missing.Path) || unresolvedDeterministicPaths.Contains(missing.Path))
            .ToArray();

        if (patchGaps.Length == 0)
        {
            await ReportActivityAsync(
                reportActivity,
                $"All {deterministicFiles.Count} file(s) generated from built-in templates.");
            return deterministicFiles;
        }

        var repoRef = new GitHubRepoRef(githubAccessToken, owner, repo, gitRef);
        var toolExecutor = new ClaudeGitHubToolExecutor(_gitHubService, _anthropic.SetupMaxToolCalls);
        var prompt = ClaudeDeploymentPrompts.BuildMissingFilesPrompt(
            owner,
            repo,
            gitRef,
            parts,
            patchGaps,
            resolvedTemplates,
            deterministicFiles);

        await ReportActivityAsync(reportActivity, "Starting Claude hybrid setup agent…");
        await ReportActivityAsync(
            reportActivity,
            $"Pre-rendered {deterministicFiles.Count} file(s); Claude will adapt {patchGaps.Length} remaining gap(s).");

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

        GeneratedDeploymentFileValidator.ValidateOrThrow(parsed);

        var aiFiles = parsed
            .Select(file => new GeneratedDeploymentFile(file.Path, file.Content))
            .ToArray();

        var forwardedPaths = patchGaps
            .Select(gap => gap.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = MergeFiles(deterministicFiles, aiFiles, forwardedPaths);
        if (merged.Count == 0)
        {
            throw new DeployAIException("setup_generation_failed", "Claude returned an empty or invalid deployment setup.");
        }

        await ReportActivityAsync(reportActivity, $"Validated {merged.Count} generated file(s).");

        return merged;
    }

    /// <summary>
    /// Merges deterministic (pre-rendered) and AI-submitted files. When
    /// <paramref name="allowedAiOverwritePaths"/> is provided, AI output can only overwrite a
    /// deterministic file if its path was actually forwarded to Claude as a gap to solve —
    /// this stops an unconstrained tool-using agent from silently clobbering an
    /// already-correct deterministic file it wasn't asked to touch. Pass null to allow AI to
    /// win on any path (used by tests exercising raw merge semantics).
    /// </summary>
    internal static IReadOnlyList<GeneratedDeploymentFile> MergeFiles(
        IReadOnlyList<GeneratedDeploymentFile> deterministicFiles,
        IReadOnlyList<GeneratedDeploymentFile> aiFiles,
        IReadOnlySet<string>? allowedAiOverwritePaths = null)
    {
        var merged = new Dictionary<string, GeneratedDeploymentFile>(StringComparer.OrdinalIgnoreCase);
        var deterministicPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in deterministicFiles)
        {
            merged[file.Path] = file;
            deterministicPaths.Add(file.Path);
        }

        foreach (var file in aiFiles)
        {
            if (allowedAiOverwritePaths is not null &&
                deterministicPaths.Contains(file.Path) &&
                !allowedAiOverwritePaths.Contains(file.Path))
            {
                continue;
            }

            merged[file.Path] = file;
        }

        return merged.Values.ToArray();
    }

    private static Task ReportActivityAsync(Func<string, Task>? reportActivity, string message) =>
        reportActivity is null ? Task.CompletedTask : reportActivity(message);
}
