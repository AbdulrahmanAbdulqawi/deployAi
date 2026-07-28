using Microsoft.Extensions.Logging;
using DeployAI.Infrastructure.Adapters;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

/// <summary>
/// Ensures a .NET server that Coolify's Nixpacks can't build (a modular monolith, or a .NET
/// version newer than Nixpacks' SDK) has a Dockerfile committed to the repo, so the app can be
/// created with the Dockerfile build pack instead. The Dockerfile is generated deterministically
/// and committed to the deployment branch as an additive artifact (idempotent: re-running upserts
/// it), so redeploys always build the current code.
/// </summary>
public interface IServerDockerfileProvisioner
{
    Task<ServerDockerfileResult?> EnsureDockerfileAsync(
        string githubToken,
        string owner,
        string repo,
        string branch,
        string rootDirectory,
        string? serviceDirectory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Same idea for a server-rendered frontend, for a different reason: Coolify's Nixpacks build
    /// receives only its own NIXPACKS_*/COOLIFY_* variables, never the app's, so values the
    /// framework inlines at build time (NEXT_PUBLIC_*) never reach the bundle and it keeps the
    /// source's localhost fallback. Owning the Dockerfile is what lets them be passed as build args.
    /// </summary>
    Task<ServerDockerfileResult?> EnsureSsrWebsiteDockerfileAsync(
        string githubToken,
        string owner,
        string repo,
        string branch,
        string appDirectory,
        IReadOnlyList<string> buildTimeEnvKeys,
        string? buildCommand,
        string? startCommand,
        string? installCommand,
        CancellationToken cancellationToken);
}

/// <summary>The build directory (Coolify base directory) and the Dockerfile path relative to it.</summary>
public sealed record ServerDockerfileResult(string BaseDirectory, string DockerfileLocation, int ExposedPort);

public sealed class ServerDockerfileProvisioner : IServerDockerfileProvisioner
{
    private readonly IGitHubService _gitHubService;
    private readonly ILogger<ServerDockerfileProvisioner> _logger;

    public ServerDockerfileProvisioner(
        IGitHubService gitHubService,
        ILogger<ServerDockerfileProvisioner> logger)
    {
        _gitHubService = gitHubService;
        _logger = logger;
    }

    public async Task<ServerDockerfileResult?> EnsureDockerfileAsync(
        string githubToken,
        string owner,
        string repo,
        string branch,
        string rootDirectory,
        string? serviceDirectory,
        CancellationToken cancellationToken)
    {
        var buildRoot = Normalize(rootDirectory);
        var serviceDir = Normalize(serviceDirectory ?? rootDirectory);

        // Find the entry csproj in the service directory.
        var serviceContents = await _gitHubService.ListAllContentsAsync(
            githubToken, owner, repo, serviceDir, branch, cancellationToken);
        var csproj = serviceContents.FirstOrDefault(item =>
            string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase) &&
            item.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

        // ListAllContentsAsync reads one level despite its name, so a directory that holds projects
        // rather than being one -- the modular monolith this generator exists for -- looks empty of
        // csproj files. Descend one level and take the web project: the entry point is the one built
        // with Microsoft.NET.Sdk.Web, and its siblings are libraries it references.
        if (csproj is null)
        {
            csproj = await FindWebProjectOneLevelDownAsync(
                githubToken, owner, repo, branch, serviceContents, cancellationToken);
        }

        if (csproj is null)
        {
            // Never silent: returning null here means the app deploys on whatever build pack it
            // already had, and the reason has to be visible or it looks like nothing was attempted.
            _logger.LogWarning(
                "No entry .csproj found under {ServiceDirectory} in {Owner}/{Repo}@{Branch}; "
                + "leaving the existing build configuration alone.",
                serviceDir,
                owner,
                repo,
                branch);
            return null;
        }

        // The project directory is wherever the csproj actually is, which is not necessarily the
        // directory we were told to look in -- and the publish path is built relative to the build
        // root, so a stale value here publishes "YemenHub.Api.csproj" instead of
        // "YemenHub.Api/YemenHub.Api.csproj" and the build fails to find the project.
        var csprojDirectory = csproj.Path.Contains('/', StringComparison.Ordinal)
            ? csproj.Path[..csproj.Path.LastIndexOf('/')]
            : string.Empty;
        serviceDir = Normalize(csprojDirectory);

        var csprojContent = await _gitHubService.GetFileContentAsync(
            githubToken, owner, repo, csproj.Path, branch, cancellationToken);

        var dockerfileContent = DotnetServerDockerfile.Build(buildRoot, serviceDir, csprojContent, csproj.Name);
        var dockerfilePath = string.IsNullOrEmpty(buildRoot) ? "Dockerfile" : $"{buildRoot}/Dockerfile";

        // Idempotent: if a Dockerfile is already there, update it (needs the blob sha).
        var existing = await _gitHubService.GetFileMetadataAsync(
            githubToken, owner, repo, dockerfilePath, branch, cancellationToken);

        await _gitHubService.UpsertFileAsync(
            githubToken,
            owner,
            repo,
            dockerfilePath,
            dockerfileContent,
            "Add Dockerfile for Coolify deployment (generated by DeployAI)",
            branch,
            existing?.Sha,
            cancellationToken);

        // Coolify's dockerfile_location is relative to the base directory (the build root).
        return new ServerDockerfileResult(
            BaseDirectory: buildRoot,
            DockerfileLocation: "/Dockerfile",
            ExposedPort: DotnetServerDockerfile.ContainerPort);
    }

    public async Task<ServerDockerfileResult?> EnsureSsrWebsiteDockerfileAsync(
        string githubToken,
        string owner,
        string repo,
        string branch,
        string appDirectory,
        IReadOnlyList<string> buildTimeEnvKeys,
        string? buildCommand,
        string? startCommand,
        string? installCommand,
        CancellationToken cancellationToken)
    {
        var appDir = Normalize(appDirectory);

        // The build context is the app directory, so its package.json is what the image installs.
        // No package.json means this isn't a Node app and generating a Dockerfile would only
        // produce a confusing build failure.
        var packageJsonPath = string.IsNullOrEmpty(appDir) ? "package.json" : $"{appDir}/package.json";
        var packageJson = await _gitHubService.GetFileContentAsync(
            githubToken, owner, repo, packageJsonPath, branch, cancellationToken);
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return null;
        }

        var lockPath = string.IsNullOrEmpty(appDir) ? "package-lock.json" : $"{appDir}/package-lock.json";
        var hasLockfile = !string.IsNullOrWhiteSpace(await _gitHubService.GetFileContentAsync(
            githubToken, owner, repo, lockPath, branch, cancellationToken));

        var dockerfileContent = SsrFrontendDockerfile.Build(
            packageJson,
            hasLockfile,
            buildTimeEnvKeys,
            buildCommand,
            startCommand,
            installCommand);

        var dockerfilePath = string.IsNullOrEmpty(appDir) ? "Dockerfile" : $"{appDir}/Dockerfile";
        var existing = await _gitHubService.GetFileMetadataAsync(
            githubToken, owner, repo, dockerfilePath, branch, cancellationToken);

        // Idempotent, and skipped entirely when the content already matches so redeploys don't
        // pile up no-op commits on the branch.
        if (existing is not null && ContentMatches(existing.Content, dockerfileContent))
        {
            return new ServerDockerfileResult(appDir, "/Dockerfile", SsrFrontendDockerfile.ContainerPort);
        }

        await _gitHubService.UpsertFileAsync(
            githubToken,
            owner,
            repo,
            dockerfilePath,
            dockerfileContent,
            "Add Dockerfile for Coolify deployment (generated by DeployAI)",
            branch,
            existing?.Sha,
            cancellationToken);

        return new ServerDockerfileResult(appDir, "/Dockerfile", SsrFrontendDockerfile.ContainerPort);
    }

    /// <summary>
    /// GitHub returns file contents base64-encoded (and line-wrapped), so compare the decoded text
    /// with line endings normalised rather than the raw payloads.
    /// </summary>
    private static bool ContentMatches(string? encodedContent, string desired)
    {
        if (string.IsNullOrWhiteSpace(encodedContent))
        {
            return false;
        }

        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(encodedContent.Replace("\n", string.Empty)));
            return string.Equals(
                decoded.Replace("\r\n", "\n").TrimEnd(),
                desired.Replace("\r\n", "\n").TrimEnd(),
                StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Looks in each immediate subdirectory for a csproj, and returns the one that is a web project.
    /// A modular monolith puts its entry project beside the libraries it references, and only the
    /// entry uses the Web SDK -- picking the first csproj found would build a class library.
    /// </summary>
    private async Task<GitHubContentItem?> FindWebProjectOneLevelDownAsync(
        string githubToken,
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubContentItem> serviceContents,
        CancellationToken cancellationToken)
    {
        foreach (var directory in serviceContents.Where(item =>
                     string.Equals(item.Type, "dir", StringComparison.OrdinalIgnoreCase)))
        {
            var inner = await _gitHubService.ListAllContentsAsync(
                githubToken, owner, repo, directory.Path, branch, cancellationToken);

            var candidate = inner.FirstOrDefault(item =>
                string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase) &&
                item.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
            if (candidate is null)
            {
                continue;
            }

            var content = await _gitHubService.GetFileContentAsync(
                githubToken, owner, repo, candidate.Path, branch, cancellationToken);
            if (content is not null &&
                content.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Normalize(string? path) =>
        path?.Trim().Replace('\\', '/').Trim('/') ?? string.Empty;
}
