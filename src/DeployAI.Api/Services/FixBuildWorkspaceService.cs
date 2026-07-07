using System.IO.Compression;
using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.GitHub;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

public interface IFixBuildWorkspaceService
{
    Task<FixBuildResult> RunBuildAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        string providerName,
        DeployTargetConfig targetConfig,
        string? framework,
        DeploymentFailureAnalysis failureAnalysis,
        IReadOnlyList<(string Path, string Content)> filePatches,
        CancellationToken cancellationToken);
}

public sealed class FixBuildWorkspaceService : IFixBuildWorkspaceService
{
    private readonly IGitHubService _gitHubService;
    private readonly IProcessBuildRunner _processBuildRunner;
    private readonly FixBuildOptions _options;

    public FixBuildWorkspaceService(
        IGitHubService gitHubService,
        IProcessBuildRunner processBuildRunner,
        IOptions<FixBuildOptions> options)
    {
        _gitHubService = gitHubService;
        _processBuildRunner = processBuildRunner;
        _options = options.Value;
    }

    public async Task<FixBuildResult> RunBuildAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        string providerName,
        DeployTargetConfig targetConfig,
        string? framework,
        DeploymentFailureAnalysis failureAnalysis,
        IReadOnlyList<(string Path, string Content)> filePatches,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new FixBuildResult(true, 0, "Local build verification is disabled.");
        }

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"deployai-fix-{Guid.NewGuid():N}");
        try
        {
            await using var zipStream = await _gitHubService.DownloadRepositoryZipballAsync(
                accessToken,
                owner,
                repo,
                gitRef,
                cancellationToken);

            var repoRoot = await ExtractZipballAsync(zipStream, workspaceRoot, cancellationToken);
            ApplyFilePatches(repoRoot, filePatches);

            string? dockerfileContent = null;
            var dockerfilePath = targetConfig.DockerfilePath;
            if (!string.IsNullOrWhiteSpace(dockerfilePath))
            {
                var fullDockerPath = Path.Combine(repoRoot, dockerfilePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullDockerPath))
                {
                    dockerfileContent = await File.ReadAllTextAsync(fullDockerPath, cancellationToken);
                }
            }
            else
            {
                var defaultDocker = Path.Combine(repoRoot, "Dockerfile");
                if (File.Exists(defaultDocker))
                {
                    dockerfileContent = await File.ReadAllTextAsync(defaultDocker, cancellationToken);
                }
            }

            var plan = FixBuildCommandResolver.Resolve(
                providerName,
                targetConfig,
                framework,
                failureAnalysis.ReferencedFiles,
                dockerfileContent);
            plan = FixBuildPlanRefiner.Refine(repoRoot, plan);

            var workingDirectory = Path.Combine(repoRoot, plan.WorkingDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(workingDirectory))
            {
                workingDirectory = repoRoot;
            }

            return await _processBuildRunner.RunAsync(
                workingDirectory,
                plan.InstallCommand,
                plan.BuildCommand,
                TimeSpan.FromMinutes(Math.Max(1, _options.TimeoutMinutes)),
                Math.Max(4096, _options.MaxOutputChars),
                cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(workspaceRoot);
        }
    }

    private static async Task<string> ExtractZipballAsync(
        Stream zipStream,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspaceRoot);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(workspaceRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var repoRoot = Directory.GetDirectories(workspaceRoot).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            throw new InvalidOperationException("Repository zipball did not contain a root directory.");
        }

        await Task.CompletedTask;
        return repoRoot;
    }

    private static void ApplyFilePatches(string repoRoot, IReadOnlyList<(string Path, string Content)> filePatches)
    {
        foreach (var (path, content) in filePatches)
        {
            var normalized = path.Replace('\\', '/').TrimStart('/');
            if (normalized.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            var fullPath = Path.Combine(repoRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
