using DeployAI.Api.Services;
using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

public class MissingConfigurationDetectorTests
{
    private readonly MissingConfigurationDetector _detector = new();

    [Fact]
    public void Detect_ReadsTheSectionFromARealDotNetStartupCrash()
    {
        // Verbatim from yemenconnect-api, which restarted until Coolify stopped it at 10 attempts.
        // Repository scanning had returned nothing for this app -- its appsettings.json is nested
        // under backend/src/YemenHub.Api and the scan only reads the repository root -- so its own
        // startup output was the only place that said what it needed.
        var found = _detector.Detect([
            "Unhandled exception. System.InvalidOperationException: Jwt configuration missing.",
            "   at Program.<Main>$(String[] args) in /src/YemenHub.Api/Program.cs:line 94",
            "   at Program.<Main>(String[] args)"
        ]);

        var jwt = Assert.Single(found);
        Assert.Equal("Jwt", jwt.Name);
        Assert.Equal(MissingConfigurationKind.ConfigurationSection, jwt.Kind);
        Assert.Contains("Jwt configuration missing", jwt.Evidence);
    }

    [Theory]
    [InlineData("Error: Missing required environment variable: STRIPE_SECRET_KEY", "STRIPE_SECRET_KEY")]
    [InlineData("Environment variable 'DATABASE_URL' is required", "DATABASE_URL")]
    [InlineData("ReferenceError: SESSION_SECRET is not defined", "SESSION_SECRET")]
    public void Detect_ReadsEnvironmentVariableNames(string line, string expected)
    {
        var found = _detector.Detect([line]);

        var variable = Assert.Single(found);
        Assert.Equal(expected, variable.Name);
        Assert.Equal(MissingConfigurationKind.EnvironmentVariable, variable.Kind);
    }

    [Fact]
    public void Detect_TreatsAQualifiedNameAsAKeyEvenWhenPhrasedAsASection()
    {
        // "Jwt__Key configuration missing" already names the key. Reporting it as a section would
        // send the UI to prefill "Jwt__Key__…", which is not a thing.
        var found = _detector.Detect(["System.InvalidOperationException: Jwt__Key configuration missing."]);

        var key = Assert.Single(found);
        Assert.Equal("Jwt__Key", key.Name);
        Assert.Equal(MissingConfigurationKind.EnvironmentVariable, key.Kind);
    }

    [Fact]
    public void Detect_ReadsAQuotedConfigurationPath()
    {
        var found = _detector.Detect(["Configuration value 'Smtp:Host' is missing"]);

        Assert.Equal("Smtp:Host", Assert.Single(found).Name);
    }

    [Fact]
    public void Detect_FindsNothingInACrashThatIsNotAboutConfiguration()
    {
        // Most crashes are not missing config, and inventing a variable to set from an unrelated
        // stack trace would be worse than staying quiet -- the user would set something arbitrary
        // and still be broken.
        var found = _detector.Detect([
            "Unhandled exception. System.NullReferenceException: Object reference not set to an instance of an object.",
            "   at YemenHub.Api.Services.EventService.Publish(Guid id)",
            "npm ERR! code ELIFECYCLE"
        ]);

        Assert.Empty(found);
    }

    [Fact]
    public void Detect_DoesNotOfferRuntimeInjectedNames()
    {
        // These are set by the platform. Asking someone to supply PORT or ASPNETCORE_ENVIRONMENT
        // is the noise the env detector already learned to filter, and it applies here too.
        var found = _detector.Detect([
            "Environment variable 'PORT' is required",
            "Environment variable 'ASPNETCORE_URLS' is required",
            "Missing required environment variable: NODE_ENV"
        ]);

        Assert.Empty(found);
    }

    [Fact]
    public void Detect_ReportsEachNameOnce_KeepingTheFirstEvidence()
    {
        // A crash-looping container repeats its startup error once per restart -- ten times, in the
        // case this was built for. The list must not repeat with it.
        var found = _detector.Detect([
            "System.InvalidOperationException: Jwt configuration missing.",
            "System.InvalidOperationException: Jwt configuration missing.",
            "System.InvalidOperationException: Jwt configuration missing."
        ]);

        Assert.Single(found);
    }

    [Fact]
    public void Detect_HandlesEmptyAndNullInput()
    {
        Assert.Empty(_detector.Detect([]));
        Assert.Empty(_detector.Detect(null!));
        Assert.Empty(_detector.Detect(["", "   "]));
    }
}
