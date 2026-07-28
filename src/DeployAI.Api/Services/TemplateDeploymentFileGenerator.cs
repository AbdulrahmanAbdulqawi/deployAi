using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

/// <summary>Generates missing setup files entirely from templates, no Claude call - the non-AI path selected when AI setup is disabled or unavailable.</summary>
public sealed class TemplateDeploymentFileGenerator : IDeploymentFileGenerator
{
    private readonly DeploymentFileScaffolder _scaffolder;
    private readonly DeploymentSetupFileFetcher _fileFetcher;

    public TemplateDeploymentFileGenerator(
        DeploymentFileScaffolder scaffolder,
        DeploymentSetupFileFetcher fileFetcher)
    {
        _scaffolder = scaffolder;
        _fileFetcher = fileFetcher;
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
        await ReportActivityAsync(reportActivity, "Loading repository files for template patches…");
        var existingFiles = await _fileFetcher.FetchGapFilesAsync(
            githubAccessToken,
            owner,
            repo,
            gitRef,
            parts,
            missingFiles,
            cancellationToken);

        return _scaffolder.ScaffoldMissingFiles(parts, missingFiles, existingFiles);
    }

    private static Task ReportActivityAsync(Func<string, Task>? reportActivity, string message) =>
        reportActivity is null ? Task.CompletedTask : reportActivity(message);
}
