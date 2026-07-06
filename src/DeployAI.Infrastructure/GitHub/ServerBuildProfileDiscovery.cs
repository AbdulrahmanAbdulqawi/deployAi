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

    public ServerBuildProfileDiscovery(IGitHubService gitHubService, IServerBuildDetector serverBuildDetector)
    {
        _gitHubService = gitHubService;
        _serverBuildDetector = serverBuildDetector;
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

        if (profile.Framework is not null || !string.IsNullOrEmpty(normalizedPath))
        {
            return profile;
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
            var candidateProfile = await DiscoverAtCandidateAsync(
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
        }

        return profile;
    }

    private async Task<ServerBuildProfile> DiscoverAtCandidateAsync(
        string accessToken,
        string owner,
        string repo,
        string candidate,
        string? gitRef,
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

        if (!ServerProjectDiscoverer.ShouldScanNestedSubdirectories(candidate))
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
            var nestedProfile = await BuildServerProfileAtPathAsync(
                accessToken,
                owner,
                repo,
                nestedPath,
                gitRef,
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
