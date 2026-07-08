using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.GitHub;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

/// <summary>
/// A reusable build workspace scoped to a single fix session. The repository is downloaded and
/// extracted once; each <see cref="RunBuildAsync"/> call re-applies the supplied file patches and
/// rebuilds against the same extracted tree, so dependency installs and downloads are not repeated.
/// </summary>
public interface IFixBuildSession : IAsyncDisposable
{
    /// <summary>True when a real workspace was provisioned (build verification enabled).</summary>
    bool IsEnabled { get; }

    /// <summary>When false, run_command and post-submit build gates are skipped.</summary>
    bool AllowsAgentBuildCommands { get; }

    Task<FixBuildResult> RunBuildAsync(
        IReadOnlyList<(string Path, string Content)> filePatches,
        CancellationToken cancellationToken);

    /// <summary>Runs a single agent-chosen command in the workspace (optionally in a subdirectory).</summary>
    Task<FixBuildResult> RunCommandAsync(
        string command,
        string? workingDirectory,
        CancellationToken cancellationToken);

    /// <summary>Writes (creates or replaces) a file in the workspace, relative to the repo root.</summary>
    void WriteFile(string path, string content);

    /// <summary>Reads a text file from the workspace, or null if missing/binary.</summary>
    string? ReadFile(string path);

    /// <summary>Lists the entries of a workspace directory as a human-readable listing.</summary>
    string ListDirectory(string path);

    /// <summary>Build/install commands used for local verification after fixes are submitted.</summary>
    FixBuildPlan? VerificationPlan { get; }
}

public interface IFixBuildWorkspaceService
{
    Task<IFixBuildSession> CreateSessionAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        string providerName,
        DeployTargetConfig targetConfig,
        string? framework,
        DeploymentFailureAnalysis failureAnalysis,
        CancellationToken cancellationToken);

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

    public async Task<IFixBuildSession> CreateSessionAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        string providerName,
        DeployTargetConfig targetConfig,
        string? framework,
        DeploymentFailureAnalysis failureAnalysis,
        CancellationToken cancellationToken)
    {
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

            return new FixBuildSession(
                _processBuildRunner,
                _options,
                workspaceRoot,
                repoRoot,
                providerName,
                targetConfig,
                framework,
                failureAnalysis);
        }
        catch
        {
            TryDeleteDirectory(workspaceRoot);
            throw;
        }
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
        await using var session = await CreateSessionAsync(
            accessToken,
            owner,
            repo,
            gitRef,
            providerName,
            targetConfig,
            framework,
            failureAnalysis,
            cancellationToken);

        return await session.RunBuildAsync(filePatches, cancellationToken);
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

    private sealed class DisabledFixBuildSession : IFixBuildSession
    {
        public static readonly DisabledFixBuildSession Instance = new();

        public bool IsEnabled => false;

        public bool AllowsAgentBuildCommands => false;

        public Task<FixBuildResult> RunBuildAsync(
            IReadOnlyList<(string Path, string Content)> filePatches,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FixBuildResult(true, 0, "Local build verification is disabled."));

        public Task<FixBuildResult> RunCommandAsync(
            string command,
            string? workingDirectory,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FixBuildResult(true, 0, "Local build verification is disabled."));

        public void WriteFile(string path, string content)
        {
            // no-op when verification is disabled
        }

        public string? ReadFile(string path) => null;

        public string ListDirectory(string path) => "Local build verification is disabled.";

        public FixBuildPlan? VerificationPlan => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FixBuildSession : IFixBuildSession
{
    private const int MaxReadFileChars = 120_000;
    private const int MaxListEntries = 300;

    private static readonly string[] DependencyManifestFiles =
    [
        "package.json",
        "package-lock.json",
        "npm-shrinkwrap.json",
        "yarn.lock",
        "pnpm-lock.yaml"
    ];

    private readonly IProcessBuildRunner _processBuildRunner;
    private readonly FixBuildOptions _options;
    private readonly string _workspaceRoot;
    private readonly string _repoRoot;
    private readonly string _providerName;
    private readonly DeployTargetConfig _targetConfig;
    private readonly string? _framework;
    private readonly DeploymentFailureAnalysis _failureAnalysis;

    private bool _dependenciesInstalled;
    private string? _installedManifestHash;
    private readonly FixBuildPlan _verificationPlan;

    public FixBuildSession(
        IProcessBuildRunner processBuildRunner,
        FixBuildOptions options,
        string workspaceRoot,
        string repoRoot,
        string providerName,
        DeployTargetConfig targetConfig,
        string? framework,
        DeploymentFailureAnalysis failureAnalysis)
    {
        _processBuildRunner = processBuildRunner;
        _options = options;
        _workspaceRoot = workspaceRoot;
        _repoRoot = repoRoot;
        _providerName = providerName;
        _targetConfig = targetConfig;
        _framework = framework;
        _failureAnalysis = failureAnalysis;
        _verificationPlan = FixBuildPlanResolver.Resolve(
            repoRoot,
            providerName,
            targetConfig,
            framework,
            failureAnalysis);
    }

    public bool IsEnabled => AllowsAgentBuildCommands;

    public bool AllowsAgentBuildCommands => _options.AllowAgentBuildCommands;

    public FixBuildPlan? VerificationPlan => AllowsAgentBuildCommands ? _verificationPlan : null;

    public async Task<FixBuildResult> RunCommandAsync(
        string command,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!AllowsAgentBuildCommands)
        {
            return new FixBuildResult(
                false,
                -1,
                "Agent build commands are disabled. Apply fixes with write_file and submit without running builds.");
        }

        var workDir = _repoRoot;
        if (!string.IsNullOrWhiteSpace(workingDirectory) &&
            TryResolveWorkspacePath(workingDirectory, out var resolved) &&
            Directory.Exists(resolved))
        {
            workDir = resolved;
        }

        var timeout = TimeSpan.FromMinutes(Math.Max(1, _options.TimeoutMinutes));
        var maxOutputChars = Math.Max(4096, _options.MaxOutputChars);
        return await _processBuildRunner.RunCommandAsync(workDir, command, timeout, maxOutputChars, cancellationToken);
    }

    public void WriteFile(string path, string content)
    {
        if (!TryResolveWorkspacePath(path, out var fullPath))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the workspace.");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    public string? ReadFile(string path)
    {
        if (!TryResolveWorkspacePath(path, out var fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        var content = File.ReadAllText(fullPath);
        if (content.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        return content.Length > MaxReadFileChars
            ? content[..MaxReadFileChars] +
              $"\n\n... truncated ({content.Length - MaxReadFileChars} additional characters not shown)"
            : content;
    }

    public string ListDirectory(string path)
    {
        var relative = (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        if (relative.Contains("..", StringComparison.Ordinal))
        {
            return "Error: path traversal is not allowed.";
        }

        var target = string.IsNullOrEmpty(relative)
            ? _repoRoot
            : Path.Combine(_repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(target))
        {
            return $"Error: directory '{(string.IsNullOrEmpty(relative) ? "/" : relative)}' was not found.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Directory: {(string.IsNullOrEmpty(relative) ? "/" : relative)}");

        var entries = Directory.EnumerateFileSystemEntries(target)
            .Select(entry =>
            {
                var name = Path.GetFileName(entry);
                var type = Directory.Exists(entry) ? "dir" : "file";
                var entryRelative = string.IsNullOrEmpty(relative) ? name : $"{relative}/{name}";
                return $"- [{type}] {entryRelative}";
            })
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListEntries)
            .ToList();

        foreach (var line in entries)
        {
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private bool TryResolveWorkspacePath(string relative, out string fullPath)
    {
        fullPath = string.Empty;
        var normalized = (relative ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var combined = Path.GetFullPath(Path.Combine(_repoRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(_repoRoot);
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = combined;
        return true;
    }

    public async Task<FixBuildResult> RunBuildAsync(
        IReadOnlyList<(string Path, string Content)> filePatches,
        CancellationToken cancellationToken)
    {
        ApplyFilePatches(_repoRoot, filePatches);

        if (!_options.VerifyOnSubmitLocally)
        {
            return new FixBuildResult(true, 0, "Post-submit local build verification is disabled.");
        }

        var plan = ResolvePlan();
        var workingDirectory = Path.Combine(_repoRoot, plan.WorkingDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(workingDirectory))
        {
            workingDirectory = _repoRoot;
        }

        var timeout = TimeSpan.FromMinutes(Math.Max(1, _options.TimeoutMinutes));
        var maxOutputChars = Math.Max(4096, _options.MaxOutputChars);
        var output = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(plan.InstallCommand))
        {
            var manifestHash = ComputeManifestHash(workingDirectory);
            var mustInstall = !_dependenciesInstalled ||
                              !string.Equals(_installedManifestHash, manifestHash, StringComparison.Ordinal);

            if (mustInstall)
            {
                var installResult = await _processBuildRunner.RunAsync(
                    workingDirectory,
                    installCommand: null,
                    buildCommand: plan.InstallCommand!,
                    timeout,
                    maxOutputChars,
                    cancellationToken);

                output.Append(installResult.Output);
                if (!installResult.Succeeded)
                {
                    return new FixBuildResult(false, installResult.ExitCode, Truncate(output.ToString(), maxOutputChars));
                }

                _dependenciesInstalled = true;
                _installedManifestHash = manifestHash;
            }
            else
            {
                output.AppendLine(
                    $"--- skipping install ({plan.InstallCommand}): dependencies already installed and manifests unchanged ---");
            }
        }

        var buildResult = await _processBuildRunner.RunAsync(
            workingDirectory,
            installCommand: null,
            buildCommand: plan.BuildCommand,
            timeout,
            maxOutputChars,
            cancellationToken);

        if (output.Length > 0)
        {
            output.AppendLine();
            output.Append(buildResult.Output);
            return new FixBuildResult(buildResult.Succeeded, buildResult.ExitCode, Truncate(output.ToString(), maxOutputChars));
        }

        return buildResult;
    }

    public ValueTask DisposeAsync()
    {
        TryDeleteDirectory(_workspaceRoot);
        return ValueTask.CompletedTask;
    }

    private FixBuildPlan ResolvePlan() =>
        FixBuildPlanResolver.Resolve(
            _repoRoot,
            _providerName,
            _targetConfig,
            _framework,
            _failureAnalysis);

    private static string ComputeManifestHash(string workingDirectory)
    {
        using var sha = SHA256.Create();
        using var stream = new MemoryStream();
        foreach (var manifest in DependencyManifestFiles)
        {
            var path = Path.Combine(workingDirectory, manifest);
            if (!File.Exists(path))
            {
                continue;
            }

            var header = Encoding.UTF8.GetBytes($"\n<<{manifest}>>\n");
            stream.Write(header, 0, header.Length);
            var bytes = File.ReadAllBytes(path);
            stream.Write(bytes, 0, bytes.Length);
        }

        stream.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(stream));
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

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars
            ? text
            : text[..maxChars] + Environment.NewLine + "[... build output truncated ...]";

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
