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
}

public sealed class ProcessBuildRunner : IProcessBuildRunner
{
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
            commandOutput.AppendLine($"Build timed out after {timeout.TotalMinutes:0} minutes.");
            sharedOutput.Append(commandOutput);
            return new FixBuildResult(false, -1, Truncate(sharedOutput.ToString(), maxOutputChars));
        }

        sharedOutput.Append(commandOutput);
        var combined = Truncate(sharedOutput.ToString(), maxOutputChars);
        return new FixBuildResult(process.ExitCode == 0, process.ExitCode, combined);
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
        text.Length <= maxChars ? text : text[..maxChars] + Environment.NewLine + "[... build output truncated ...]";
}
