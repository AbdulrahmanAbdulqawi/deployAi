using DeployAI.Api.Services.Checks;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// Whether the running app still has the settings the deployed code needs.
/// </summary>
/// <remarks>
/// DeployAI already worked this out at deploy time and threw the answer away, which made it a
/// one-shot: a key someone deleted in the provider's UI three weeks after a green deploy was
/// invisible forever. These cover the standing version, which re-asks using the recorded manifest
/// and costs no repository reads.
/// </remarks>
public class ConfigurationCheckTests
{
    private static readonly Guid TargetId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task AMissingRequiredSetting_Fails_AndNamesIt()
    {
        var checks = await RunAsync(
            required: ["Jwt__SigningKey", "ConnectionStrings__Default"],
            present: [Var("ConnectionStrings__Default", "Host=db")]);

        var required = Single(checks, "config.required");
        Assert.Equal(VerificationCheckStatus.Failed, required.Status);
        Assert.Contains("Jwt__SigningKey", required.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings__Default", required.Message, StringComparison.Ordinal);
    }

    /// <summary>The case the standing check exists for: it was fine at deploy time and is not now.</summary>
    [Fact]
    public async Task ASettingDeletedAfterTheDeploy_Fails()
    {
        var checks = await RunAsync(
            required: ["Jwt__SigningKey"],
            present: [],
            fingerprints: new Dictionary<string, string>
            {
                ["Jwt__SigningKey"] = ConfigValueFingerprint.Compute(TargetId, "Jwt__SigningKey", "secret")
            });

        Assert.Equal(VerificationCheckStatus.Failed, Single(checks, "config.required").Status);
    }

    [Fact]
    public async Task EverythingPresent_Passes()
    {
        var checks = await RunAsync(
            required: ["Jwt__SigningKey"],
            present: [Var("Jwt__SigningKey", "secret")]);

        Assert.Equal(VerificationCheckStatus.Passed, Single(checks, "config.required").Status);
    }

    /// <summary>
    /// A manifest captured from a scan that could not read the repository keeps saying so. Treating
    /// it as "nothing required" would turn one blind read into a permanent all-clear.
    /// </summary>
    [Fact]
    public async Task AManifestFromABlindScan_IsInconclusive_NotPassed()
    {
        var checks = await RunAsync(
            required: [],
            present: [],
            wasInconclusive: true,
            inconclusiveReason: "could not read tester/app@main");

        var required = Single(checks, "config.required");
        Assert.Equal(VerificationCheckStatus.Inconclusive, required.Status);
        Assert.NotEqual(VerificationCheckStatus.Passed, required.Status);
        Assert.Contains("could not read tester/app@main", required.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// No manifest yet means nothing is wrong and nothing is being retried — a skip, not a blind spot.
    /// </summary>
    [Fact]
    public async Task ATargetWithNoManifestYet_IsSkipped()
    {
        var checks = await RunAsync(required: null, present: []);

        var required = Single(checks, "config.required");
        Assert.Equal(VerificationCheckStatus.Skipped, required.Status);
        Assert.NotEqual(VerificationCheckStatus.Inconclusive, required.Status);
    }

    [Fact]
    public async Task AnUnreadableProvider_IsInconclusive_NotAMissingSetting()
    {
        var checks = await RunAsync(
            required: ["Jwt__SigningKey"],
            present: [],
            providerThrows: true);

        var required = Single(checks, "config.required");
        Assert.Equal(VerificationCheckStatus.Inconclusive, required.Status);
        Assert.NotEqual(VerificationCheckStatus.Failed, required.Status);
    }

    /// <summary>
    /// A value edited in the provider's UI is a legitimate thing to do, and nothing else would ever
    /// say it happened — so it warns rather than failing.
    /// </summary>
    [Fact]
    public async Task AValueChangedSinceTheDeploy_Warns()
    {
        var checks = await RunAsync(
            required: ["ConnectionStrings__Default"],
            present: [Var("ConnectionStrings__Default", "Host=staging")],
            fingerprints: new Dictionary<string, string>
            {
                ["ConnectionStrings__Default"] =
                    ConfigValueFingerprint.Compute(TargetId, "ConnectionStrings__Default", "Host=prod")
            });

        var drift = Single(checks, "config.drift");
        Assert.Equal(VerificationCheckStatus.Warning, drift.Status);
        Assert.Contains("ConnectionStrings__Default", drift.Message, StringComparison.Ordinal);
        // The current value is never quoted back — the fingerprint exists so it need not be stored.
        Assert.DoesNotContain("Host=staging", drift.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnchangedValue_Passes()
    {
        var checks = await RunAsync(
            required: ["ConnectionStrings__Default"],
            present: [Var("ConnectionStrings__Default", "Host=prod")],
            fingerprints: new Dictionary<string, string>
            {
                ["ConnectionStrings__Default"] =
                    ConfigValueFingerprint.Compute(TargetId, "ConnectionStrings__Default", "Host=prod")
            });

        Assert.Equal(VerificationCheckStatus.Passed, Single(checks, "config.drift").Status);
    }

    /// <summary>
    /// With nothing recorded to compare against, drift is not reported at all — a permanent
    /// "couldn't check" row on every sweep forever would be noise, not information.
    /// </summary>
    [Fact]
    public async Task NoRecordedFingerprints_ProducesNoDriftCheck()
    {
        var checks = await RunAsync(
            required: ["Jwt__SigningKey"],
            present: [Var("Jwt__SigningKey", "secret")]);

        Assert.DoesNotContain(checks, c => c.CheckId.StartsWith("config.drift", StringComparison.Ordinal));
    }

    private static ProjectVerificationCheck Single(
        IReadOnlyList<ProjectVerificationCheck> checks,
        string prefix) =>
        Assert.Single(checks.Where(c => c.CheckId.StartsWith(prefix, StringComparison.Ordinal)));

    private static ProviderEnvVar Var(string key, string value) =>
        new(Guid.NewGuid().ToString(), key, value, "plain", [], ValueHidden: false);

    private static async Task<IReadOnlyList<ProjectVerificationCheck>> RunAsync(
        IReadOnlyList<string>? required,
        IReadOnlyList<ProviderEnvVar> present,
        IDictionary<string, string>? fingerprints = null,
        bool wasInconclusive = false,
        string? inconclusiveReason = null,
        bool providerThrows = false)
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new DeployAIDbContext(options);

        var credential = new ProviderCredential { Id = Guid.NewGuid(), ProviderName = "coolify" };
        var project = new Project { Id = Guid.NewGuid(), Name = "app", GitHubRepoFullName = "tester/app" };
        var target = new DeployTarget
        {
            Id = TargetId,
            ProjectId = project.Id,
            ProviderName = "coolify",
            CredentialId = credential.Id,
            Credential = credential,
            ProviderProjectId = "app-uuid",
            ConfigJson = """{"role":"server"}"""
        };

        if (required is not null)
        {
            var manifest = new TargetConfigManifest
            {
                DeployTargetId = target.Id,
                ProjectId = project.Id,
                Branch = "main",
                WasInconclusive = wasInconclusive,
                InconclusiveReason = inconclusiveReason,
                CapturedAt = DateTimeOffset.UtcNow
            };
            manifest.SetRequiredKeys(required);
            manifest.SetValueFingerprints(fingerprints ?? new Dictionary<string, string>());
            db.TargetConfigManifests.Add(manifest);
            await db.SaveChangesAsync();
        }

        var management = new Mock<IProviderManagement>();
        var listing = management.Setup(m => m.ListEnvVarsAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));

        if (providerThrows)
        {
            listing.ThrowsAsync(new HttpRequestException("unreachable"));
        }
        else
        {
            listing.ReturnsAsync(present);
        }

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement(It.IsAny<string>())).Returns(management.Object);

        var check = new ConfigurationCheck(db, factory.Object, CheckTestData.Tokens());
        return await check.ContributeAsync(
            new ProjectCheckContext(project, [target], null), CancellationToken.None);
    }
}

/// <summary>
/// The fingerprint that lets DeployAI notice a value changed without storing the value.
/// </summary>
public class ConfigValueFingerprintTests
{
    [Fact]
    public void TheSameValue_FingerprintsTheSame()
    {
        var target = Guid.NewGuid();

        Assert.Equal(
            ConfigValueFingerprint.Compute(target, "Key", "value"),
            ConfigValueFingerprint.Compute(target, "Key", "value"));
    }

    [Fact]
    public void ADifferentValue_FingerprintsDifferently()
    {
        var target = Guid.NewGuid();

        Assert.NotEqual(
            ConfigValueFingerprint.Compute(target, "Key", "before"),
            ConfigValueFingerprint.Compute(target, "Key", "after"));
    }

    /// <summary>
    /// Keyed per target so a low-entropy value — "Production", "true", a port — is not recoverable by
    /// spotting the same fingerprint under two different apps.
    /// </summary>
    [Fact]
    public void TheSameValueUnderTwoTargets_FingerprintsDifferently()
    {
        Assert.NotEqual(
            ConfigValueFingerprint.Compute(Guid.NewGuid(), "ASPNETCORE_ENVIRONMENT", "Production"),
            ConfigValueFingerprint.Compute(Guid.NewGuid(), "ASPNETCORE_ENVIRONMENT", "Production"));
    }

    /// <summary>The value must not be reconstructable from what is stored.</summary>
    [Fact]
    public void TheFingerprint_DoesNotContainTheValue()
    {
        const string secret = "super-secret-signing-key";

        var fingerprint = ConfigValueFingerprint.Compute(Guid.NewGuid(), "Jwt__SigningKey", secret);

        Assert.DoesNotContain(secret, fingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, fingerprint.Length);
    }
}
