using DeployAI.Core.Deployments;

namespace DeployAI.Infrastructure.GitHub;

public interface IServerBuildProfileDiscovery
{
    Task<ServerBuildProfile> DiscoverAsync(
        string accessToken,
        string owner,
        string repo,
        string normalizedPath,
        string? gitRef,
        CancellationToken cancellationToken);
}

public sealed class ServerBuildProfileDiscovery : IServerBuildProfileDiscovery
{
    private readonly IGitHubService _gitHubService;
    private readonly IServerBuildDetector _serverBuildDetector;
    private readonly IRepositoryLayoutResolver _layoutResolver;

    public ServerBuildProfileDiscovery(
        IGitHubService gitHubService,
        IServerBuildDetector serverBuildDetector,
        IRepositoryLayoutResolver layoutResolver)
    {
        _gitHubService = gitHubService;
        _serverBuildDetector = serverBuildDetector;
        _layoutResolver = layoutResolver;
    }

    public async Task<ServerBuildProfile> DiscoverAsync(
        string accessToken,
        string owner,
        string repo,
        string normalizedPath,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        var profile = await BuildServerProfileAtPathAsync(
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

        if (!string.IsNullOrEmpty(normalizedPath))
        {
            // The user named this directory, so the question is only "where inside it" -- which is
            // the shared resolver's job. Until now this read the named directory and nothing else,
            // so choosing "backend" or "backend/src" in the wizard returned no framework and no
            // commands: the screen pre-filled nothing and looked like the repo was unrecognised.
            return await DiscoverBelowAsync(
                accessToken, owner, repo, normalizedPath, gitRef, profile, cancellationToken);
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

        foreach (var candidate in ServerProjectDiscoverer.RankCandidates(rootDirectories))
        {
            // Depth 2 lets a nested layout like backend/src/YemenHub.Api resolve: the candidate
            // (backend) is scanned, then its container children (src), then theirs (YemenHub.Api).
            var candidateProfile = await DiscoverAtCandidateAsync(
                accessToken,
                owner,
                repo,
                candidate,
                gitRef,
                nestedDepthRemaining: 2,
                cancellationToken);
            if (candidateProfile.Framework is not null)
            {
                return candidateProfile;
            }
        }

        return profile;
    }

    /// <summary>
    /// Looks for the app inside a directory the caller named, through the shared resolver.
    /// </summary>
    /// <remarks>
    /// Deliberately not used for the whole-repository case above. That one asks a different
    /// question -- <em>which</em> of several sibling directories is the server -- and answers it by
    /// ranking names and excluding known frontend directories. The resolver has no such opinion and
    /// takes the first directory holding any application manifest, so at a repository root it would
    /// nominate <c>client/package.json</c> and classify the Angular app as the backend. Ranking
    /// cannot move into the resolver either: the storage and configuration scans must read the
    /// frontend, and skipping it there would be the same bug pointed the other way.
    /// </remarks>
    private async Task<ServerBuildProfile> DiscoverBelowAsync(
        string accessToken,
        string owner,
        string repo,
        string normalizedPath,
        string? gitRef,
        ServerBuildProfile profileAtPath,
        CancellationToken cancellationToken)
    {
        var layout = await _layoutResolver.ResolveAsync(
            accessToken, owner, repo, gitRef, normalizedPath, cancellationToken);

        if (string.Equals(layout.ProjectDirectory, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            // Nothing below it either -- the answer for this directory really is "no framework".
            return profileAtPath;
        }

        var nested = await BuildServerProfileAtPathAsync(
            accessToken, owner, repo, layout.ProjectDirectory, gitRef, cancellationToken);

        return nested.Framework is not null ? nested : profileAtPath;
    }

    private async Task<ServerBuildProfile> DiscoverAtCandidateAsync(
        string accessToken,
        string owner,
        string repo,
        string candidate,
        string? gitRef,
        int nestedDepthRemaining,
        CancellationToken cancellationToken)
    {
        var candidateProfile = await BuildServerProfileAtPathAsync(
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

        if (nestedDepthRemaining <= 0 || !ServerProjectDiscoverer.ShouldScanNestedSubdirectories(candidate))
        {
            return candidateProfile;
        }

        var candidateContents = await _gitHubService.ListAllContentsAsync(
            accessToken,
            owner,
            repo,
            candidate,
            gitRef,
            cancellationToken);
        var subdirectories = candidateContents
            .Where(item => string.Equals(item.Type, "dir", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .ToList();

        foreach (var nestedPath in ServerProjectDiscoverer.ExpandNestedCandidates(candidate, subdirectories))
        {
            var nestedProfile = await DiscoverAtCandidateAsync(
                accessToken,
                owner,
                repo,
                nestedPath,
                gitRef,
                nestedDepthRemaining - 1,
                cancellationToken);
            if (nestedProfile.Framework is not null)
            {
                return nestedProfile;
            }
        }

        return candidateProfile;
    }

    private async Task<ServerBuildProfile> BuildServerProfileAtPathAsync(
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
        var files = contents
            .Where(item => string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hasDockerfile = files.Any(item => string.Equals(item.Name, "Dockerfile", StringComparison.OrdinalIgnoreCase));
        var dockerfilePath = files.FirstOrDefault(item => string.Equals(item.Name, "Dockerfile", StringComparison.OrdinalIgnoreCase))?.Path;
        var packagePath = files.FirstOrDefault(item => string.Equals(item.Name, "package.json", StringComparison.OrdinalIgnoreCase))?.Path;
        var csprojPath = files.FirstOrDefault(item => item.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))?.Path;
        var requirementsPath = files.FirstOrDefault(item => string.Equals(item.Name, "requirements.txt", StringComparison.OrdinalIgnoreCase))?.Path;
        var pyprojectPath = files.FirstOrDefault(item => string.Equals(item.Name, "pyproject.toml", StringComparison.OrdinalIgnoreCase))?.Path;
        var goModPath = files.FirstOrDefault(item => string.Equals(item.Name, "go.mod", StringComparison.OrdinalIgnoreCase))?.Path;
        var cargoPath = files.FirstOrDefault(item => string.Equals(item.Name, "Cargo.toml", StringComparison.OrdinalIgnoreCase))?.Path;

        var dockerfileContent = dockerfilePath is null
            ? null
            : await _gitHubService.GetFileContentAsync(accessToken, owner, repo, dockerfilePath, gitRef, cancellationToken);
        var packageJson = packagePath is null
            ? null
            : await _gitHubService.GetFileContentAsync(accessToken, owner, repo, packagePath, gitRef, cancellationToken);
        var csprojContent = csprojPath is null
            ? null
            : await _gitHubService.GetFileContentAsync(accessToken, owner, repo, csprojPath, gitRef, cancellationToken);
        var requirementsTxt = requirementsPath is null
            ? null
            : await _gitHubService.GetFileContentAsync(accessToken, owner, repo, requirementsPath, gitRef, cancellationToken);
        var pyprojectToml = pyprojectPath is null
            ? null
            : await _gitHubService.GetFileContentAsync(accessToken, owner, repo, pyprojectPath, gitRef, cancellationToken);
        var goMod = goModPath is null
            ? null
            : await _gitHubService.GetFileContentAsync(accessToken, owner, repo, goModPath, gitRef, cancellationToken);
        var cargoToml = cargoPath is null
            ? null
            : await _gitHubService.GetFileContentAsync(accessToken, owner, repo, cargoPath, gitRef, cancellationToken);

        return _serverBuildDetector.Detect(
            normalizedPath,
            hasDockerfile,
            dockerfileContent,
            packageJson,
            csprojContent,
            requirementsTxt,
            pyprojectToml,
            goMod,
            cargoToml);
    }
}
