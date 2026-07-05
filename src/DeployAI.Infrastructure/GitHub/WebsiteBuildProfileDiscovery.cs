using DeployAI.Core.Deployments;

namespace DeployAI.Infrastructure.GitHub;

public interface IWebsiteBuildProfileDiscovery
{
    Task<FrontendBuildProfile?> DiscoverAsync(
        string accessToken,
        string owner,
        string repo,
        string normalizedPath,
        string? gitRef,
        CancellationToken cancellationToken);
}

public sealed class WebsiteBuildProfileDiscovery : IWebsiteBuildProfileDiscovery
{
    private readonly IGitHubService _gitHubService;
    private readonly IFrontendBuildDetector _frontendBuildDetector;

    public WebsiteBuildProfileDiscovery(IGitHubService gitHubService, IFrontendBuildDetector frontendBuildDetector)
    {
        _gitHubService = gitHubService;
        _frontendBuildDetector = frontendBuildDetector;
    }

    public async Task<FrontendBuildProfile?> DiscoverAsync(
        string accessToken,
        string owner,
        string repo,
        string normalizedPath,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        var profile = await BuildWebsiteProfileAtPathAsync(
            accessToken,
            owner,
            repo,
            normalizedPath,
            gitRef,
            cancellationToken);

        if (profile.Framework is not null)
        {
            return profile;
        }

        var initialContents = await _gitHubService.ListAllContentsAsync(
            accessToken, owner, repo, normalizedPath, gitRef, cancellationToken);
        if (HasStaticSiteSignal(initialContents))
        {
            return profile;
        }

        if (!string.IsNullOrEmpty(normalizedPath))
        {
            return null;
        }

        var rootContents = await _gitHubService.ListAllContentsAsync(
            accessToken,
            owner,
            repo,
            string.Empty,
            gitRef,
            cancellationToken);
        var rootDirectories = rootContents
            .Where(item => string.Equals(item.Type, "dir", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .ToList();

        foreach (var candidate in WebsiteProjectDiscoverer.RankCandidates(rootDirectories))
        {
            var candidateProfile = await BuildWebsiteProfileAtPathAsync(
                accessToken,
                owner,
                repo,
                candidate,
                gitRef,
                cancellationToken);
            if (candidateProfile.Framework is not null)
            {
                return candidateProfile;
            }

            var candidateContents = await _gitHubService.ListAllContentsAsync(
                accessToken, owner, repo, candidate, gitRef, cancellationToken);
            if (HasStaticSiteSignal(candidateContents))
            {
                return candidateProfile;
            }
        }

        var rootContentsForStatic = await _gitHubService.ListAllContentsAsync(
            accessToken, owner, repo, string.Empty, gitRef, cancellationToken);
        if (HasStaticSiteSignal(rootContentsForStatic))
        {
            return profile;
        }

        return profile.Framework is not null ? profile : null;
    }

    private async Task<FrontendBuildProfile> BuildWebsiteProfileAtPathAsync(
        string accessToken,
        string owner,
        string repo,
        string normalizedPath,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        var contents = await _gitHubService.ListAllContentsAsync(
            accessToken,
            owner,
            repo,
            normalizedPath,
            gitRef,
            cancellationToken);

        if (!WebsiteProjectDiscoverer.HasWebsiteSignals(contents))
        {
            return new FrontendBuildProfile(normalizedPath, "npm run build", "npm install", "dist", null);
        }

        var angularPath = string.IsNullOrEmpty(normalizedPath) ? "angular.json" : $"{normalizedPath}/angular.json";
        var packagePath = string.IsNullOrEmpty(normalizedPath) ? "package.json" : $"{normalizedPath}/package.json";

        var angularJson = await _gitHubService.GetFileContentAsync(accessToken, owner, repo, angularPath, gitRef, cancellationToken);
        var packageJson = await _gitHubService.GetFileContentAsync(accessToken, owner, repo, packagePath, gitRef, cancellationToken);

        return _frontendBuildDetector.Detect(normalizedPath, angularJson, packageJson);
    }

    private static bool HasStaticSiteSignal(IReadOnlyList<GitHubContentItem> contents) =>
        contents.Any(item =>
            string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name, "index.html", StringComparison.OrdinalIgnoreCase));
}
