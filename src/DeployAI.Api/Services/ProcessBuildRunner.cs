using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeployAI.Api.Services;

public interface IProcessBuildRunner
{
    Task<FixBuildResult> RunAsync(
        string workingDirectory,
        string? installCommand,
        string buildCommand,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken cancellationToken);

    /// <summary>Runs a single arbitrary shell command in a hardened, secret-free child process.</summary>
    Task<FixBuildResult> RunCommandAsync(
        string workingDirectory,
        string command,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken cancellationToken);
}

public sealed class ProcessBuildRunner : IProcessBuildRunner
{
    // Only these host environment variables are forwarded to build/command processes. Everything
    // else (application secrets such as Anthropic__ApiKey, ConnectionStrings__*, GitHub__*, etc.) is
    // deliberately dropped so agent-chosen commands cannot read them.
    private static readonly string[] AllowedWindowsEnvKeys =
    [
        "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE", "COMSPEC",
        "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)",
        "PROGRAMW6432", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE"
    ];

    private static readonly string[] AllowedUnixEnvKeys =
    [
        "PATH", "HOME", "TMPDIR", "LANG", "LC_ALL", "LC_CTYPE", "USER", "SHELL", "TERM"
    ];

    public async Task<FixBuildResult> RunAsync(
        string workingDirectory,
        string? installCommand,
        string buildCommand,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(installCommand))
        {
            var installResult = await RunSingleCommandAsync(
                workingDirectory,
                installCommand,
                timeout,
                maxOutputChars,
                output,
                cancellationToken);
            if (!installResult.Succeeded)
            {
                return installResult;
            }
        }

        return await RunSingleCommandAsync(
            workingDirectory,
            buildCommand,
            timeout,
            maxOutputChars,
            output,
            cancellationToken);
    }

    public Task<FixBuildResult> RunCommandAsync(
        string workingDirectory,
        string command,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken cancellationToken) =>
        RunSingleCommandAsync(
            workingDirectory,
            command,
            timeout,
            maxOutputChars,
            new StringBuilder(),
            cancellationToken);

    private static async Task<FixBuildResult> RunSingleCommandAsync(
        string workingDirectory,
        string command,
        TimeSpan timeout,
        int maxOutputChars,
        StringBuilder sharedOutput,
        CancellationToken cancellationToken)
    {
        if (sharedOutput.Length > 0)
        {
            sharedOutput.AppendLine();
        }

        sharedOutput.AppendLine($"--- running: {command} ---");

        var commandOutput = new StringBuilder();
        using var process = new Process();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-lc \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        ApplyHardenedEnvironment(process.StartInfo);

        process.Start();
        var readStdout = ReadStreamAsync(process.StandardOutput, commandOutput, maxOutputChars, cancellationToken);
        var readStderr = ReadStreamAsync(process.StandardError, commandOutput, maxOutputChars, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(readStdout, readStderr);
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore kill failures
            }

            commandOutput.AppendLine();
            commandOutput.AppendLine($"Command timed out after {timeout.TotalMinutes:0} minutes.");
            sharedOutput.Append(commandOutput);
            return new FixBuildResult(false, -1, Truncate(sharedOutput.ToString(), maxOutputChars));
        }

        sharedOutput.Append(commandOutput);
        var combined = Truncate(sharedOutput.ToString(), maxOutputChars);
        return new FixBuildResult(process.ExitCode == 0, process.ExitCode, combined);
    }

    /// <summary>
    /// Builds a scrubbed child-process environment: only an allow-list of essential system variables
    /// is forwarded (so tooling can be found on PATH), plus persistent package-manager cache
    /// directories. Application secrets inherited by the API process are intentionally excluded so
    /// arbitrary agent-chosen commands cannot exfiltrate them.
    /// </summary>
    private static void ApplyHardenedEnvironment(ProcessStartInfo startInfo)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var allowedKeys = isWindows ? AllowedWindowsEnvKeys : AllowedUnixEnvKeys;

        var forwarded = new Dictionary<string, string>(
            isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var key in allowedKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
            {
                forwarded[key] = value;
            }
        }

        startInfo.Environment.Clear();
        foreach (var (key, value) in forwarded)
        {
            startInfo.Environment[key] = value;
        }

        var cacheRoot = Path.Combine(Path.GetTempPath(), "deployai-build-cache");
        var npmCache = Path.Combine(cacheRoot, "npm");
        var yarnCache = Path.Combine(cacheRoot, "yarn");
        var pnpmStore = Path.Combine(cacheRoot, "pnpm-store");
        var nugetPackages = Path.Combine(cacheRoot, "nuget");

        try
        {
            Directory.CreateDirectory(npmCache);
            Directory.CreateDirectory(yarnCache);
            Directory.CreateDirectory(pnpmStore);
            Directory.CreateDirectory(nugetPackages);
        }
        catch
        {
            // If the cache directories cannot be created, fall back to package-manager defaults.
        }

        startInfo.Environment["npm_config_cache"] = npmCache;
        startInfo.Environment["YARN_CACHE_FOLDER"] = yarnCache;
        startInfo.Environment["npm_config_store_dir"] = pnpmStore;
        startInfo.Environment["NUGET_PACKAGES"] = nugetPackages;
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        StringBuilder buffer,
        int maxOutputChars,
        CancellationToken cancellationToken)
    {
        while (buffer.Length < maxOutputChars)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            buffer.AppendLine(line);
        }
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + Environment.NewLine + "[... output truncated ...]";
}
