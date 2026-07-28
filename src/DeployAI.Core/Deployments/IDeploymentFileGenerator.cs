namespace DeployAI.Core.Deployments;

/// <summary>Generates the content of missing split-origin setup files for a repo (template-based or AI-based, per implementation).</summary>
public interface IDeploymentFileGenerator
{
    /// <summary>Generates content for each missing file, optionally reporting progress as it works.</summary>
    Task<IReadOnlyList<GeneratedDeploymentFile>> GenerateMissingFilesAsync(
        string owner,
        string repo,
        string gitRef,
        string githubAccessToken,
        IReadOnlyList<DeploymentPlanPart> parts,
        IReadOnlyList<MissingDeploymentFile> missingFiles,
        Func<string, Task>? reportActivity,
        CancellationToken cancellationToken);
}
