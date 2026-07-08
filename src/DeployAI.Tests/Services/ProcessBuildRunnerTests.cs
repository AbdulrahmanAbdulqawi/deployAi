using System.Runtime.InteropServices;
using DeployAI.Api.Services;

namespace DeployAI.Tests.Services;

public class ProcessBuildRunnerTests
{
    [Fact]
    public async Task RunCommandAsync_Completes_WhenOutputExceedsMaxChars()
    {
        var runner = new ProcessBuildRunner();
        var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "for /L %i in (1,1,8000) do @echo line%i"
            : "for i in $(seq 1 8000); do echo line$i; done";

        var result = await runner.RunCommandAsync(
            Environment.CurrentDirectory,
            command,
            TimeSpan.FromMinutes(2),
            maxOutputChars: 4096,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Output);
        Assert.Contains("line8000", result.Output, StringComparison.Ordinal);
        Assert.Contains("[... output truncated ...]", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("line1", result.Output, StringComparison.Ordinal);
    }
}
