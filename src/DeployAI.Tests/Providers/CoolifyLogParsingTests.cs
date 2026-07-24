using DeployAI.Providers.Coolify;

namespace DeployAI.Tests.Providers;

public class CoolifyLogParsingTests
{
    // Coolify returns logs as a JSON array, not text. Splitting it on newlines put raw JSON in
    // front of the user for the whole deployment.
    private const string RealLog = """
    [
      {"command":null,"output":"Starting deployment of yemeni-breeze to localhost.","type":"stdout","hidden":false,"batch":1},
      {"command":"docker stop --time=30 abc","output":"Flag --time has been deprecated","type":"stdout","hidden":true,"batch":23},
      {"command":null,"output":"web  Built\n api  Built","type":"stdout","hidden":false,"batch":19},
      {"command":null,"output":"New container started.","type":"stdout","hidden":false,"batch":1}
    ]
    """;

    [Fact]
    public void ParseLogEntries_ReturnsOutputLines_NotJson()
    {
        var lines = CoolifyProvider.ParseLogEntries(RealLog);

        Assert.Contains("Starting deployment of yemeni-breeze to localhost.", lines);
        Assert.Contains("New container started.", lines);
        Assert.DoesNotContain(lines, line => line.Contains("\"output\":", StringComparison.Ordinal));
    }

    // A single entry can carry several lines; they should arrive as separate log lines.
    [Fact]
    public void ParseLogEntries_SplitsMultiLineOutput()
    {
        var lines = CoolifyProvider.ParseLogEntries(RealLog);

        Assert.Contains("web  Built", lines);
        Assert.Contains("api  Built", lines);
    }

    // Coolify hides its own bookkeeping commands in its UI; so do we.
    [Fact]
    public void ParseLogEntries_SkipsHiddenEntries()
    {
        var lines = CoolifyProvider.ParseLogEntries(RealLog);

        Assert.DoesNotContain(lines, line => line.Contains("--time has been deprecated", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseLogEntries_FallsBackToLineSplitting_ForPlainText()
    {
        var lines = CoolifyProvider.ParseLogEntries("first line\nsecond line");

        Assert.Equal(["first line", "second line"], lines);
    }

    // Polling mid-write is normal; a truncated body must not throw.
    [Fact]
    public void ParseLogEntries_ToleratesAPartiallyWrittenLog()
    {
        var lines = CoolifyProvider.ParseLogEntries("""[{"output":"half a lin""");

        Assert.Empty(lines);
    }

    [Fact]
    public void ParseLogEntries_HandlesEmptyInput()
    {
        Assert.Empty(CoolifyProvider.ParseLogEntries(null));
        Assert.Empty(CoolifyProvider.ParseLogEntries("  "));
    }
}
