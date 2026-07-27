using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

/// <summary>Fetches the existing content of files needed for setup file generation/patching (both missing files' neighbors and any files being patched in place).</summary>
public sealed class DeploymentSetupFileFetcher
{
    private readonly IGitHubService _gitHubService;

    public DeploymentSetupFileFetcher(IGitHubService gitHubService)
    {
        _gitHubService = gitHubService;
    }

    /// <summary>Fetches every file relevant to closing the readiness gap - missing files' existing (possibly absent) content, plus files that need patching.</summary>
    internal async Task<IReadOnlyDictionary<string, string?>> FetchGapFilesAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        IReadOnlyList<DeploymentPlanPart> parts,
        IReadOnlyList<MissingDeploymentFile> missingFiles,
        CancellationToken cancellationToken)
    {
        var website = SplitOriginDetection.FindWebsitePart(parts);
        var server = SplitOriginDetection.FindServerPart(parts);
        if (website is null || server is null)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        var paths = missingFiles
            .Select(missing => missing.Path)
            .Concat(SplitOriginReadinessEvaluator.BuildAllScanPaths(website, server))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var fileContents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            fileContents[path] = await TryGetFileContentAsync(accessToken, owner, repo, path, gitRef, cancellationToken);
        }

        return fileContents;
    }

    // A transient/permission error on one scan path (often unrelated to the actual gaps
    // being generated) must not abort the entire setup flow — mirrors
    // ClaudeDeploymentFixGenerator.TryReadFileAsync's per-file isolation.
    private async Task<string?> TryGetFileContentAsync(
        string accessToken,
        string owner,
        string repo,
        string path,
        string gitRef,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _gitHubService.GetFileContentAsync(accessToken, owner, repo, path, gitRef, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
