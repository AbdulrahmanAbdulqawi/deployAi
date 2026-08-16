using DeployAI.Api.Controllers;
using DeployAI.Api.Services;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Controllers;

/// <summary>
/// Connecting and disconnecting a DNS account.
/// </summary>
/// <remarks>
/// The disconnect tests carry the most weight. There is no foreign key on
/// <c>ProjectDomain.DnsCredentialId</c>, so nothing in the database protects those rows — and the
/// obviously-simple behaviours are both wrong: refusing to disconnect while any domain uses the
/// connection would trap the user forever, and deleting every record would take live sites down as
/// a side effect of a settings action.
/// </remarks>
public class DnsControllerTests
{
    private const string Token = "cfut_a_believable_token";

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public byte[] Encrypt(string plainText) => System.Text.Encoding.UTF8.GetBytes(plainText);
        public string Decrypt(byte[] cipherText) => System.Text.Encoding.UTF8.GetString(cipherText);
    }

    private sealed class Harness
    {
        public required DeployAIDbContext Db { get; init; }
        public required DnsController Controller { get; init; }
        public required Mock<IDnsZoneProvider> Provider { get; init; }
        public required Guid UserId { get; init; }
        public required Guid ProjectId { get; init; }
        public required Guid DeployTargetId { get; init; }
    }

    private static DnsZone ReadyZone(string name = "example.com") =>
        new("zone-1", name, null, DnsZoneUsability.Ready, "Ready.", "Acme");

    private static Harness CreateHarness(DnsCredentialCheck? check = null)
    {
        var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var userId = Guid.NewGuid();
        var credential = new ProviderCredential
        {
            Id = Guid.NewGuid(), UserId = userId, ProviderName = "coolify",
            Kind = CredentialKind.Deployment, Label = "Default", TokenEncrypted = []
        };
        var project = new Project
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "breeze",
            GitHubRepoFullName = "owner/breeze", DefaultBranch = "main"
        };
        var target = new DeployTarget
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, CredentialId = credential.Id,
            ProviderName = "coolify", ProviderProjectId = "app", ConfigJson = """{"role":"website"}"""
        };

        db.ProviderCredentials.Add(credential);
        db.Projects.Add(project);
        db.DeployTargets.Add(target);
        db.SaveChanges();

        var provider = new Mock<IDnsZoneProvider>();
        provider.SetupGet(p => p.ProviderName).Returns("cloudflare");
        provider.SetupGet(p => p.DisplayName).Returns("Cloudflare");
        provider.SetupGet(p => p.CredentialFields)
            .Returns([new DnsCredentialField("token", "API token", true)]);
        provider
            .Setup(p => p.PackCredential(It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Returns((IReadOnlyDictionary<string, string> f) =>
                new ProviderCredentials(f.TryGetValue("token", out var v) ? v : string.Empty));
        provider
            .Setup(p => p.ValidateCredentialsAsync(It.IsAny<ProviderCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(check ?? new DnsCredentialCheck(
                DnsCredentialVerdict.Ok, "Connected.", [ReadyZone()]));
        provider
            .Setup(p => p.DeleteRecordAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var factory = new Mock<IDnsZoneProviderFactory>();
        factory.Setup(f => f.GetZoneProvider("cloudflare")).Returns(provider.Object);
        factory.SetupGet(f => f.All).Returns([provider.Object]);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.UserId).Returns(userId);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens
            .Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Token);

        return new Harness
        {
            Db = db,
            Provider = provider,
            UserId = userId,
            ProjectId = project.Id,
            DeployTargetId = target.Id,
            Controller = new DnsController(
                db, currentUser.Object, factory.Object, tokens.Object,
                new PassthroughEncryption(), NullLogger<DnsController>.Instance)
        };
    }

    private static Dictionary<string, string> Fields(string token) => new() { ["token"] = token };

    private static ProviderCredential SeedConnection(Harness harness)
    {
        var credential = new ProviderCredential
        {
            Id = Guid.NewGuid(),
            UserId = harness.UserId,
            ProviderName = "cloudflare",
            Kind = CredentialKind.Dns,
            Label = "Default",
            TokenEncrypted = System.Text.Encoding.UTF8.GetBytes(Token),
            IsValid = true
        };
        harness.Db.ProviderCredentials.Add(credential);
        harness.Db.SaveChanges();
        return credential;
    }

    private static ProjectDomain SeedDomain(Harness harness, Guid credentialId, DomainStatus status)
    {
        var domain = new ProjectDomain
        {
            Id = Guid.NewGuid(),
            ProjectId = harness.ProjectId,
            DeployTargetId = harness.DeployTargetId,
            Hostname = $"{status.ToString().ToLowerInvariant()}.example.com",
            DisplayHostname = "x",
            Source = DomainSource.ManagedZone,
            Status = status,
            DnsCredentialId = credentialId,
            ZoneId = "zone-1",
            ManagedRecordId = "record-1",
            StatusMessage = "seeded"
        };
        harness.Db.ProjectDomains.Add(domain);
        harness.Db.SaveChanges();
        return domain;
    }

    // ---- connecting ------------------------------------------------------

    [Fact]
    public async Task CreateConnection_StoresTheTokenAsADnsCredential()
    {
        var harness = CreateHarness();

        var result = await harness.Controller.CreateConnection(
            new DnsController.CreateDnsConnectionRequest(Fields(Token)), CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        var stored = harness.Db.ProviderCredentials.Single(c => c.Kind == CredentialKind.Dns);
        Assert.Equal("cloudflare", stored.ProviderName);
        Assert.True(stored.IsValid);
        Assert.Equal(Token, System.Text.Encoding.UTF8.GetString(stored.TokenEncrypted));
    }

    [Fact]
    public async Task CreateConnection_PersistsTheTokenExpiry_SoItCanBeReplacedBeforeItLapses()
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var harness = CreateHarness(new DnsCredentialCheck(
            DnsCredentialVerdict.Ok, "Connected.", [ReadyZone()], TokenExpiresOn: expiry));

        await harness.Controller.CreateConnection(
            new DnsController.CreateDnsConnectionRequest(Fields(Token)), CancellationToken.None);

        Assert.Equal(expiry, harness.Db.ProviderCredentials.Single(c => c.Kind == CredentialKind.Dns).ExpiresAt);
    }

    // Nothing may be written until the provider confirms the token actually works.
    [Theory]
    [InlineData(DnsCredentialVerdict.Malformed, "dns_token_malformed")]
    [InlineData(DnsCredentialVerdict.Rejected, "dns_token_rejected")]
    [InlineData(DnsCredentialVerdict.CannotListZones, "dns_token_cannot_list_zones")]
    [InlineData(DnsCredentialVerdict.NoZonesVisible, "dns_no_zones_visible")]
    public async Task CreateConnection_PersistsNothing_WhenTheTokenIsRefused(
        DnsCredentialVerdict verdict, string expectedCode)
    {
        var harness = CreateHarness(new DnsCredentialCheck(verdict, "no good", []));

        var ex = await Assert.ThrowsAsync<DeployAIException>(() => harness.Controller.CreateConnection(
            new DnsController.CreateDnsConnectionRequest(Fields(Token)), CancellationToken.None));

        Assert.Equal(expectedCode, ex.ErrorCode);
        Assert.Empty(harness.Db.ProviderCredentials.Where(c => c.Kind == CredentialKind.Dns));
    }

    // "We could not check" is not "your token is bad", and must not be reported as one.
    [Theory]
    [InlineData(DnsCredentialVerdict.RateLimited, DnsErrorCodes.RateLimited)]
    [InlineData(DnsCredentialVerdict.Unreachable, DnsErrorCodes.Unreachable)]
    public async Task CreateConnection_SavesNothingAndBlamesNobody_WhenTheCheckCouldNotRun(
        DnsCredentialVerdict verdict, string expectedCode)
    {
        var harness = CreateHarness(new DnsCredentialCheck(verdict, "could not check", []));

        var ex = await Assert.ThrowsAsync<DeployAIException>(() => harness.Controller.CreateConnection(
            new DnsController.CreateDnsConnectionRequest(Fields(Token)), CancellationToken.None));

        Assert.Equal(expectedCode, ex.ErrorCode);
        Assert.Empty(harness.Db.ProviderCredentials.Where(c => c.Kind == CredentialKind.Dns));
    }

    [Fact]
    public async Task CreateConnection_UpdatesInPlace_RatherThanCollidingOnTheUniqueIndex()
    {
        var harness = CreateHarness();
        SeedConnection(harness);

        var result = await harness.Controller.CreateConnection(
            new DnsController.CreateDnsConnectionRequest(Fields("cfut_a_replacement_token")), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var stored = Assert.Single(harness.Db.ProviderCredentials.Where(c => c.Kind == CredentialKind.Dns));
        Assert.Equal("cfut_a_replacement_token", System.Text.Encoding.UTF8.GetString(stored.TokenEncrypted));
    }

    // The unique index is (UserId, ProviderName, Label) and does not include Kind, so a clash with
    // another kind of connection has to be caught rather than left to surface as internal_error.
    [Fact]
    public async Task CreateConnection_RefusesALabelAlreadyUsedByAnotherKindOfConnection()
    {
        var harness = CreateHarness();
        harness.Db.ProviderCredentials.Add(new ProviderCredential
        {
            Id = Guid.NewGuid(), UserId = harness.UserId, ProviderName = "cloudflare",
            Kind = CredentialKind.ObjectStorage, Label = "Default", TokenEncrypted = []
        });
        harness.Db.SaveChanges();

        var ex = await Assert.ThrowsAsync<DeployAIException>(() => harness.Controller.CreateConnection(
            new DnsController.CreateDnsConnectionRequest(Fields(Token)), CancellationToken.None));

        Assert.Equal("dns_label_in_use", ex.ErrorCode);
    }

    [Fact]
    public async Task ListConnections_ShowsOnlyDnsConnections()
    {
        var harness = CreateHarness();
        SeedConnection(harness);

        var result = Assert.IsType<OkObjectResult>(
            await harness.Controller.ListConnections(CancellationToken.None));

        var connections = result.Value!.GetType().GetProperty("connections")!.GetValue(result.Value);
        var list = Assert.IsAssignableFrom<IEnumerable<DnsController.DnsConnectionSummary>>(connections).ToList();
        Assert.Single(list);
        Assert.Equal("cloudflare", list[0].ProviderName);
    }

    // The Kind filter is a security boundary: without it these routes reach deployment credentials
    // by id and delete them past the guard that stops an app losing its connection.
    [Fact]
    public async Task Disconnect_RefusesToTouchADeploymentCredential()
    {
        var harness = CreateHarness();
        var deploymentCredential = harness.Db.ProviderCredentials.First(c => c.Kind == CredentialKind.Deployment);

        var ex = await Assert.ThrowsAsync<DeployAIException>(
            () => harness.Controller.Disconnect(deploymentCredential.Id, CancellationToken.None));

        Assert.Equal("not_found", ex.ErrorCode);
        Assert.NotNull(harness.Db.ProviderCredentials.Find(deploymentCredential.Id));
    }

    // ---- disconnecting ---------------------------------------------------

    // Deleting these records would take working sites offline as a side effect of removing a
    // connection — so the record stays and the domain is handed back to the user.
    [Theory]
    [InlineData(DomainStatus.Active)]
    [InlineData(DomainStatus.CertificatePending)]
    [InlineData(DomainStatus.Assigned)]
    [InlineData(DomainStatus.DnsVerified)]
    public async Task Disconnect_HandsOverALiveDomain_WithoutDeletingItsRecord(DomainStatus status)
    {
        var harness = CreateHarness();
        var credential = SeedConnection(harness);
        var domain = SeedDomain(harness, credential.Id, status);

        await harness.Controller.Disconnect(credential.Id, CancellationToken.None);

        harness.Provider.Verify(p => p.DeleteRecordAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        var updated = harness.Db.ProjectDomains.AsNoTracking().Single(d => d.Id == domain.Id);
        Assert.Equal(DomainSource.UserProvided, updated.Source);
        Assert.Null(updated.DnsCredentialId);
        Assert.Null(updated.ManagedRecordId);
        Assert.Equal(status, updated.Status);
        Assert.Contains("yours to maintain", updated.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DomainStatus.Pending)]
    [InlineData(DomainStatus.DnsPending)]
    [InlineData(DomainStatus.DnsFailed)]
    public async Task Disconnect_ReleasesTheRecordOfADomainThatIsNotLiveYet(DomainStatus status)
    {
        var harness = CreateHarness();
        var credential = SeedConnection(harness);
        SeedDomain(harness, credential.Id, status);

        await harness.Controller.Disconnect(credential.Id, CancellationToken.None);

        harness.Provider.Verify(p => p.DeleteRecordAsync(
            It.IsAny<ProviderCredentials>(), "zone-1", "record-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // Blocking would tell someone they may never disconnect Cloudflare because they once used it.
    [Fact]
    public async Task Disconnect_SucceedsEvenWhileDomainsDependOnIt()
    {
        var harness = CreateHarness();
        var credential = SeedConnection(harness);
        SeedDomain(harness, credential.Id, DomainStatus.Active);

        Assert.IsType<NoContentResult>(
            await harness.Controller.Disconnect(credential.Id, CancellationToken.None));
        Assert.Null(harness.Db.ProviderCredentials.Find(credential.Id));
    }

    [Fact]
    public async Task Disconnect_LeavesNoDomainPointingAtACredentialThatIsGone()
    {
        var harness = CreateHarness();
        var credential = SeedConnection(harness);
        SeedDomain(harness, credential.Id, DomainStatus.Active);
        SeedDomain(harness, credential.Id, DomainStatus.DnsPending);

        await harness.Controller.Disconnect(credential.Id, CancellationToken.None);

        Assert.Empty(harness.Db.ProjectDomains.AsNoTracking().Where(d => d.DnsCredentialId != null));
    }

    // A record we could not tidy up must not block the removal the user asked for.
    [Fact]
    public async Task Disconnect_CompletesEvenWhenReleasingARecordFails()
    {
        var harness = CreateHarness();
        harness.Provider
            .Setup(p => p.DeleteRecordAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cloudflare said no"));
        var credential = SeedConnection(harness);
        SeedDomain(harness, credential.Id, DomainStatus.DnsPending);

        Assert.IsType<NoContentResult>(
            await harness.Controller.Disconnect(credential.Id, CancellationToken.None));
        Assert.Null(harness.Db.ProviderCredentials.Find(credential.Id));
    }

    [Fact]
    public async Task PreviewDisconnect_SaysWhatHappensToEachDomain()
    {
        var harness = CreateHarness();
        var credential = SeedConnection(harness);
        SeedDomain(harness, credential.Id, DomainStatus.Active);
        SeedDomain(harness, credential.Id, DomainStatus.DnsPending);

        var result = Assert.IsType<OkObjectResult>(
            await harness.Controller.PreviewDisconnect(credential.Id, CancellationToken.None));
        var impact = Assert.IsType<DnsController.DisconnectImpact>(result.Value);

        Assert.Equal(2, impact.DependentCount);
        Assert.Single(impact.KeepWorking);
        Assert.Single(impact.WillBeReleased);
        Assert.Contains("keep working", impact.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewDisconnect_SaysSoWhenNothingDependsOnIt()
    {
        var harness = CreateHarness();
        var credential = SeedConnection(harness);

        var result = Assert.IsType<OkObjectResult>(
            await harness.Controller.PreviewDisconnect(credential.Id, CancellationToken.None));
        var impact = Assert.IsType<DnsController.DisconnectImpact>(result.Value);

        Assert.Equal(0, impact.DependentCount);
        Assert.Contains("changes nothing", impact.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- re-checking a stored connection ---------------------------------

    [Fact]
    public async Task ListZones_MarksAConnectionInvalid_WhenTheTokenHasStoppedWorking()
    {
        var harness = CreateHarness(new DnsCredentialCheck(
            DnsCredentialVerdict.Rejected, "Cloudflare does not recognise that token.", []));
        var credential = SeedConnection(harness);

        await harness.Controller.ListZones(credential.Id, CancellationToken.None);

        Assert.False(harness.Db.ProviderCredentials.AsNoTracking().Single(c => c.Id == credential.Id).IsValid);
    }

    // A network blip must not condemn a working connection.
    [Theory]
    [InlineData(DnsCredentialVerdict.RateLimited)]
    [InlineData(DnsCredentialVerdict.Unreachable)]
    public async Task ListZones_LeavesTheConnectionAlone_WhenTheCheckCouldNotRun(DnsCredentialVerdict verdict)
    {
        var harness = CreateHarness(new DnsCredentialCheck(verdict, "could not check", []));
        var credential = SeedConnection(harness);

        await harness.Controller.ListZones(credential.Id, CancellationToken.None);

        Assert.True(harness.Db.ProviderCredentials.AsNoTracking().Single(c => c.Id == credential.Id).IsValid);
    }
}
