using DeployAI.Api.Services;
using DeployAI.Api.Services.Checks;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// Whether the provider still has the application a deploy target points at.
/// </summary>
/// <remarks>
/// The distinction every test here turns on is the one the underlying capability was written for:
/// "the app was deleted" and "DeployAI could not look" must never produce the same verdict, because
/// one tells a user to go and redeploy and the other tells them nothing at all.
/// </remarks>
public class ProviderExistenceCheckTests
{
    [Fact]
    public async Task AnAbsentApplication_Fails_AndSaysItNoLongerExists()
    {
        var checks = await RunAsync(new ProviderApplicationExistence(
            ProviderApplicationPresence.Absent, null, null,
            "The application this app deploys to no longer exists on Coolify."));

        var check = Assert.Single(checks);
        Assert.Equal(VerificationCheckStatus.Failed, check.Status);
        Assert.Contains("no longer exists", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The most important negative in this change: an unreachable provider must never be reported as
    /// a deleted app.
    /// </summary>
    [Fact]
    public async Task AnUnknownPresence_IsInconclusive_NotFailed()
    {
        var checks = await RunAsync(new ProviderApplicationExistence(
            ProviderApplicationPresence.Unknown, null, null,
            "DeployAI could not reach Coolify to check this app (HttpRequestException)."));

        var check = Assert.Single(checks);
        Assert.Equal(VerificationCheckStatus.Inconclusive, check.Status);
        Assert.NotEqual(VerificationCheckStatus.Failed, check.Status);
    }

    /// <summary>
    /// A container Coolify has given up restarting is present and dead. Coolify stops an application
    /// entirely once it has crash-looped past its restart limit, so "exists but exited" is exactly
    /// the shape of an app that died and stayed dead.
    /// </summary>
    [Fact]
    public async Task AStoppedApplication_Fails()
    {
        var checks = await RunAsync(new ProviderApplicationExistence(
            ProviderApplicationPresence.Present, "exited:unhealthy", null, "It exists."));

        Assert.Equal(VerificationCheckStatus.Failed, Assert.Single(checks).Status);
    }

    /// <summary>A deploy in flight resolves on its own, so it warns rather than failing.</summary>
    [Fact]
    public async Task ADeployingApplication_Warns()
    {
        var checks = await RunAsync(new ProviderApplicationExistence(
            ProviderApplicationPresence.Present, "deploying", null, "It exists."));

        Assert.Equal(VerificationCheckStatus.Warning, Assert.Single(checks).Status);
    }

    [Fact]
    public async Task ARunningApplication_Passes()
    {
        var checks = await RunAsync(new ProviderApplicationExistence(
            ProviderApplicationPresence.Present, "running:healthy", "https://api.example.com", "It exists."));

        var check = Assert.Single(checks);
        Assert.Equal(VerificationCheckStatus.Passed, check.Status);
        Assert.Equal("https://api.example.com", check.Url);
    }

    /// <summary>
    /// A provider with no way to answer is skipped, not inconclusive: no retry and no fix would ever
    /// change the result, so presenting it as a temporary blind spot would be misleading.
    /// </summary>
    [Fact]
    public async Task AProviderWithoutTheCapability_IsSkipped_NotInconclusive()
    {
        var checks = await RunAsync(existence: null);

        var check = Assert.Single(checks);
        Assert.Equal(VerificationCheckStatus.Skipped, check.Status);
        Assert.NotEqual(VerificationCheckStatus.Inconclusive, check.Status);
    }

    /// <summary>A database is not an application; asking the applications endpoint about one would 404 meaninglessly.</summary>
    [Fact]
    public async Task ADatabaseTarget_IsNotAskedAbout()
    {
        var checks = await RunAsync(
            new ProviderApplicationExistence(ProviderApplicationPresence.Present, "running", null, "."),
            role: DeploymentPartRoles.Database);

        Assert.Empty(checks);
    }

    private static async Task<IReadOnlyList<ProjectVerificationCheck>> RunAsync(
        ProviderApplicationExistence? existence,
        string role = DeploymentPartRoles.Server)
    {
        var factory = new Mock<IProviderApplicationExistenceFactory>();

        if (existence is not null)
        {
            var provider = new Mock<IProviderApplicationExistence>();
            provider.Setup(p => p.CheckApplicationExistsAsync(
                    It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(existence);
            factory.Setup(f => f.GetApplicationExistence(It.IsAny<string>())).Returns(provider.Object);
        }
        else
        {
            factory.Setup(f => f.GetApplicationExistence(It.IsAny<string>()))
                .Returns((IProviderApplicationExistence?)null);
        }

        var check = new ProviderExistenceCheck(factory.Object, CheckTestData.Tokens());
        return await check.ContributeAsync(CheckTestData.Context(role), CancellationToken.None);
    }
}

/// <summary>
/// Whether an app's own output says it is working.
/// </summary>
/// <remarks>
/// The scan behind this already ran on every deploy and already found real failures; what it never
/// did was produce a verdict anyone would see an hour later. These tests pin the three answers it
/// must be able to give apart from each other — clean, broken, and unreadable.
/// </remarks>
public class RuntimeExceptionVerificationCheckTests
{
    [Fact]
    public async Task ACleanLog_Passes()
    {
        var checks = await RunAsync(new RuntimeExceptionScan([], Inconclusive: false, null));

        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(checks).Status);
    }

    /// <summary>
    /// A stopped container produces the same empty finding list as a healthy one. Only one of them is
    /// good news, and the scan already distinguishes them — this makes the verdict do the same.
    /// </summary>
    [Fact]
    public async Task AnInconclusiveScan_IsInconclusive_NotPassed()
    {
        var checks = await RunAsync(
            new RuntimeExceptionScan([], Inconclusive: true, "the container returned no output"));

        var check = Assert.Single(checks);
        Assert.Equal(VerificationCheckStatus.Inconclusive, check.Status);
        Assert.NotEqual(VerificationCheckStatus.Passed, check.Status);
        // The reason is carried through verbatim: "could not check" is only actionable when it says
        // what got in the way.
        Assert.Contains("the container returned no output", check.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The repeat count is the signal. One exception at startup is noise; the same one four hundred
    /// times is an endpoint that is down for everyone using it — which is exactly how yemenConnect's
    /// landing-page endpoint failed for the entire life of a deployment while /health returned 200.
    /// </summary>
    [Fact]
    public async Task FindingsFail_AndNameTheMostRepeatedFirst()
    {
        var checks = await RunAsync(new RuntimeExceptionScan(
            [
                new RuntimeExceptionFinding("System.NullReferenceException: Object reference not set", 2),
                new RuntimeExceptionFinding("System.InvalidOperationException: The LINQ expression could not be translated", 412)
            ],
            Inconclusive: false,
            null));

        var check = Assert.Single(checks);
        Assert.Equal(VerificationCheckStatus.Failed, check.Status);
        Assert.Contains("×412", check.Message, StringComparison.Ordinal);
        Assert.True(
            check.Message.IndexOf("LINQ", StringComparison.Ordinal) <
            check.Message.IndexOf("NullReference", StringComparison.Ordinal),
            "The most repeated failure should be named first.");
    }

    /// <summary>
    /// Railway and Vercel expose no container output at all. Reporting a pass there would be a check
    /// that looks like evidence and is not.
    /// </summary>
    [Fact]
    public async Task AProviderWithNoLogCapability_IsSkipped_NotPassed()
    {
        var checks = await RunAsync(
            new RuntimeExceptionScan([], Inconclusive: false, null), hasRuntimeLogs: false);

        var check = Assert.Single(checks);
        Assert.Equal(VerificationCheckStatus.Skipped, check.Status);
        Assert.Contains("does not expose container output", check.Message, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<ProjectVerificationCheck>> RunAsync(
        RuntimeExceptionScan scan,
        bool hasRuntimeLogs = true)
    {
        var scanner = new Mock<IRuntimeExceptionCheck>();
        scanner.Setup(s => s.ScanAsync(
                It.IsAny<DeployTarget>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(scan);

        var logsFactory = new Mock<IProviderRuntimeLogsFactory>();
        logsFactory.Setup(f => f.GetRuntimeLogs(It.IsAny<string>()))
            .Returns(hasRuntimeLogs ? new Mock<IProviderRuntimeLogs>().Object : null);

        var check = new RuntimeExceptionVerificationCheck(
            scanner.Object, logsFactory.Object, Options.Create(new FleetVerificationOptions()));

        return await check.ContributeAsync(
            CheckTestData.Context(DeploymentPartRoles.Server), CancellationToken.None);
    }
}

/// <summary>
/// Whether the connections a project's targets depend on still work.
/// </summary>
public class ProviderConnectionCheckTests
{
    /// <summary>
    /// An object-storage credential is not a deployment connection and must not be checked as one.
    /// </summary>
    /// <remarks>
    /// Found on the first live sweep. Object storage and DNS are deliberately not deployment
    /// providers — that separation is what keeps them out of deploy-target pickers — so asking the
    /// deployment factory about a Hetzner storage credential returns nothing, and this check reported
    /// it as "DeployAI could not reach hetzner-storage". A permanent inconclusive row that no action
    /// would ever clear is exactly the noise that teaches people to ignore the whole screen.
    /// </remarks>
    [Fact]
    public async Task AStorageTargetsCredential_IsNotCheckedAsADeploymentConnection()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new DeployAIDbContext(options);

        var context = CheckTestData.Context(DeploymentPartRoles.Storage);

        var factory = new Mock<IProviderFactory>();
        var cache = new Mock<IProviderCredentialValidationCache>();

        var check = new ProviderConnectionCheck(
            db, factory.Object, CheckTestData.Tokens(), cache.Object);
        var checks = await check.ContributeAsync(context, CancellationToken.None);

        Assert.Empty(checks);
        // And it never even asked, so a storage credential cannot be reported as an unreachable
        // deployment connection.
        cache.Verify(
            c => c.GetOrValidateAsync(
                It.IsAny<Guid>(),
                It.IsAny<Func<CancellationToken, Task<bool?>>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

/// <summary>Shared scaffolding for the check contributors: one project, one server deploy target.</summary>
internal static class CheckTestData
{
    public static ProjectCheckContext Context(string role)
    {
        var projectId = Guid.NewGuid();
        var credential = new ProviderCredential
        {
            Id = Guid.NewGuid(),
            ProviderName = "coolify",
            Label = "Default"
        };

        var target = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ProviderName = "coolify",
            CredentialId = credential.Id,
            Credential = credential,
            ProviderProjectId = "app-uuid-1",
            ConfigJson = $$"""{"role":"{{role}}"}"""
        };

        var project = new Project
        {
            Id = projectId,
            Name = "test",
            GitHubRepoFullName = "tester/test"
        };

        return new ProjectCheckContext(project, [target], DeploymentId: null);
    }

    public static IProviderCredentialTokenService Tokens()
    {
        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");
        return tokens.Object;
    }
}
