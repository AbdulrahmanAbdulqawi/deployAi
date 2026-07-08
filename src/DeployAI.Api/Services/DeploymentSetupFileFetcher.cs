using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

public sealed class DeploymentSetupFileFetcher
{
    private readonly IGitHubService _gitHubService;

    public DeploymentSetupFileFetcher(IGitHubService gitHubService)
    {
        _gitHubService = gitHubService;
    }

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
            fileContents[path] = await _gitHubService.GetFileContentAsync(
                accessToken,
                owner,
                repo,
                path,
                gitRef,
                cancellationToken);
        }

        return fileContents;
    }
}
