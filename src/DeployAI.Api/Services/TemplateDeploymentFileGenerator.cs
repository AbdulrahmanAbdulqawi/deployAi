using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

public sealed class TemplateDeploymentFileGenerator : IDeploymentFileGenerator
{
    public Task<IReadOnlyList<GeneratedDeploymentFile>> GenerateMissingFilesAsync(
        string owner,
        string repo,
        string gitRef,
        string githubAccessToken,
        IReadOnlyList<DeploymentPlanPart> parts,
        IReadOnlyList<MissingDeploymentFile> missingFiles,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken)
    {
        _ = owner;
        _ = repo;
        _ = gitRef;
        _ = githubAccessToken;
        _ = reportActivity;
        _ = cancellationToken;
        return Task.FromResult(DeploymentFileScaffolder.ScaffoldMissingFiles(parts, missingFiles, existingFilesByPath: null));
    }
}
