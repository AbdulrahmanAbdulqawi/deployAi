using DeployAI.Core.Deployments.Graph;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.Graph;

public class EnvVarDetectorTests
{
    // Taken from yemenBreeze's real docker-compose.coolify.yml — the file this detector must
    // read correctly for the live end-to-end to work.
    private const string YemenBreezeCompose = """
        services:
          api:
            build: ./server/YemeniBreeze.Api
            environment:
              - ConnectionStrings__Default=Data Source=/data/yemenibreeze.db
              - Jwt__Key=${JWT_KEY}
              - Admin__Email=${ADMIN_EMAIL}
              - Admin__Password=${ADMIN_PASSWORD}
              - Cors__Origins__0=${SITE_ORIGIN}
              - Email__SmtpHost=${EMAIL_SMTP_HOST:-smtp-relay.brevo.com}
              - Email__SmtpPort=${EMAIL_SMTP_PORT:-587}
              - Email__SmtpUser=${EMAIL_SMTP_USER}
              - Email__SmtpKey=${EMAIL_SMTP_KEY}
              - Email__FromAddress=${EMAIL_FROM_ADDRESS}
              - Email__PublicBaseUrl=${SITE_ORIGIN}
              - Storage__Endpoint=${STORAGE_ENDPOINT}
              - Storage__Bucket=${STORAGE_BUCKET}
              - Storage__AccessKey=${STORAGE_ACCESS_KEY}
              - Storage__SecretKey=${STORAGE_SECRET_KEY}
        """;

    private readonly EnvVarDetector _detector = new();

    [Fact]
    public void Detect_ReadsYemenBreezeCompose_Completely()
    {
        var vars = _detector.Detect(new EnvScanInputs(ComposeContent: YemenBreezeCompose));
        var byName = vars.ToDictionary(v => v.Name);

        // SITE_ORIGIN appears twice (CORS + email base URL) and must dedupe to one entry.
        Assert.Single(vars, v => v.Name == "SITE_ORIGIN");

        Assert.True(byName["JWT_KEY"].IsSecret);
        Assert.True(byName["ADMIN_PASSWORD"].IsSecret);
        Assert.True(byName["STORAGE_SECRET_KEY"].IsSecret);
        Assert.False(byName["ADMIN_EMAIL"].IsSecret);

        // ${VAR:-default} carries its default; plain ${VAR} is required.
        Assert.True(byName["EMAIL_SMTP_HOST"].HasDefault);
        Assert.Equal("smtp-relay.brevo.com", byName["EMAIL_SMTP_HOST"].DefaultValue);
        Assert.Equal("587", byName["EMAIL_SMTP_PORT"].DefaultValue);
        Assert.False(byName["JWT_KEY"].HasDefault);
    }

    [Fact]
    public void Detect_ClassifiesForSmartFill()
    {
        var vars = _detector.Detect(new EnvScanInputs(ComposeContent: YemenBreezeCompose));
        var byName = vars.ToDictionary(v => v.Name);

        Assert.Equal(EnvVarCategory.Storage, byName["STORAGE_ENDPOINT"].Category);
        Assert.Equal(EnvVarCategory.Storage, byName["STORAGE_BUCKET"].Category);
        Assert.Equal(EnvVarCategory.AdminEmail, byName["ADMIN_EMAIL"].Category);
        Assert.Equal(EnvVarCategory.Domain, byName["SITE_ORIGIN"].Category);
        Assert.Equal(EnvVarCategory.Generic, byName["EMAIL_SMTP_USER"].Category);
    }

    // DATABASE_URL ends in _URL; misfiling it as Domain would smart-fill it with the site
    // domain — precisely wrong. Order of classification checks is what this pins.
    [Theory]
    [InlineData("DATABASE_URL", EnvVarCategory.Database)]
    [InlineData("REDIS_URL", EnvVarCategory.Database)]
    [InlineData("ConnectionStrings__Default", EnvVarCategory.Database)]
    [InlineData("S3_ENDPOINT", EnvVarCategory.Storage)]
    [InlineData("PUBLIC_BASE_URL", EnvVarCategory.Domain)]
    public void Classify_ChecksSpecificCategoriesFirst(string name, EnvVarCategory expected)
    {
        Assert.Equal(expected, EnvVarDetector.Classify(name));
    }

    [Fact]
    public void Detect_MergesMentionsAcrossSources()
    {
        var vars = _detector.Detect(new EnvScanInputs(
            ComposeContent: "  - Jwt__Key=${JWT_KEY}",
            DotEnvExampleContent: "JWT_KEY=\nAPI_RATE_LIMIT=100",
            GithubWorkflowContents: ["run: deploy\nenv:\n  JWT_KEY: ${{ secrets.JWT_KEY }}"]));

        var jwt = Assert.Single(vars, v => v.Name == "JWT_KEY");
        Assert.Equal(["compose", "dotenv", "github-actions"], jwt.SeenIn.Order().ToArray()
            is var s && s.SequenceEqual(["compose", "dotenv", "github-actions"]) ? s : jwt.SeenIn.Order().ToArray());

        var rateLimit = Assert.Single(vars, v => v.Name == "API_RATE_LIMIT");
        Assert.Equal("100", rateLimit.DefaultValue);
    }

    // "changeme" and "your-key-here" are instructions, not defaults — treating them as values
    // would deploy an app with a literal placeholder secret.
    [Fact]
    public void DotEnvPlaceholders_AreRequired_NotDefaults()
    {
        var vars = _detector.Detect(new EnvScanInputs(
            DotEnvExampleContent: "API_KEY=changeme\nSMTP_HOST=your-smtp-host\nREAL_DEFAULT=587"));

        Assert.False(vars.Single(v => v.Name == "API_KEY").HasDefault);
        Assert.False(vars.Single(v => v.Name == "SMTP_HOST").HasDefault);
        Assert.True(vars.Single(v => v.Name == "REAL_DEFAULT").HasDefault);
    }

    [Fact]
    public void AppsettingsEmptyLeaves_BecomeSectionKeyNames()
    {
        var vars = _detector.Detect(new EnvScanInputs(AppsettingsContent: """
            {
              "Logging": { "LogLevel": { "Default": "" } },
              "Jwt": { "Key": "" },
              "Email": { "SmtpHost": "smtp.example.com", "SmtpKey": "" }
            }
            """));

        var names = vars.Select(v => v.Name).ToArray();
        Assert.Contains("Jwt__Key", names);
        Assert.Contains("Email__SmtpKey", names);
        // Populated values are real config; Logging is framework noise.
        Assert.DoesNotContain("Email__SmtpHost", names);
        Assert.DoesNotContain(names, n => n.StartsWith("Logging"));
    }

    // Platform-injected names must never be asked of the user.
    [Fact]
    public void PlatformVariables_AreExcluded()
    {
        var vars = _detector.Detect(new EnvScanInputs(ComposeContent: """
            environment:
              - X=${COOLIFY_URL}
              - Y=${SERVICE_FQDN_WEB}
              - Z=${PORT}
              - K=${JWT_KEY}
            """));

        Assert.Single(vars);
        Assert.Equal("JWT_KEY", vars[0].Name);
    }

    [Fact]
    public void Detect_OrdersRequiredSecretsFirst()
    {
        var vars = _detector.Detect(new EnvScanInputs(ComposeContent: YemenBreezeCompose));

        Assert.True(vars[0].IsSecret && !vars[0].HasDefault);
        // Defaults sink to the bottom.
        Assert.True(vars[^1].HasDefault || !vars[^1].IsSecret);
    }

    [Fact]
    public void ReadmeFencedBlocks_AreScanned_ProseIsNot()
    {
        var vars = _detector.Detect(new EnvScanInputs(ReadmeContent: """
            Set these before deploying:

            ```
            APP_SECRET=
            ```

            NOT_A_VAR=this line is prose outside the fence
            """));

        Assert.Single(vars);
        Assert.Equal("APP_SECRET", vars[0].Name);
    }
}
