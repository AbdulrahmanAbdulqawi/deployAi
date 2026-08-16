using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// The gate this whole feature exists for: a domain is never written to the provider with an
/// https:// scheme until DNS has been shown to reach the server. Writing it early makes the proxy
/// start a certificate challenge that cannot succeed, which spends one of Let's Encrypt's five
/// failed validations an hour and leaves a self-signed certificate serving behind a deploy that
/// reported success.
/// </summary>
public class DomainServiceTests
{
    private const string ServerIp = "46.225.80.188";
    private const string Hostname = "app.example.com";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness
    {
        public required DeployAIDbContext Db { get; init; }
        public required DomainService Service { get; init; }
        public required Mock<IDnsResolver> Dns { get; init; }
        public required Mock<ICertificateInspector> Certificates { get; init; }
        public required Mock<IApplicationDomainAssignment> Assignment { get; init; }
        public required Mock<IDeploymentOrchestrator> Orchestrator { get; init; }
        public required FixedClock Clock { get; init; }
        public required Guid DomainId { get; init; }

        public ProjectDomain Domain => Db.ProjectDomains.AsTracking().First(d => d.Id == DomainId);
    }

    private static Harness CreateHarness(DomainStatus status, string? expectedAddress = ServerIp)
    {
        var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var userId = Guid.NewGuid();
        var credential = new ProviderCredential
        {
            Id = Guid.NewGuid(), UserId = userId, ProviderName = "coolify", TokenEncrypted = []
        };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "breeze",
            GitHubRepoFullName = "owner/breeze",
            DefaultBranch = "main"
        };
        var target = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            CredentialId = credential.Id,
            ProviderName = "coolify",
            ProviderProjectId = "app-compose",
            ConfigJson = """{"role":"website","composeFileLocation":"docker-compose.coolify.yml","domainServiceName":"web"}"""
        };
        var domain = new ProjectDomain
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            DeployTargetId = target.Id,
            Hostname = Hostname,
            DisplayHostname = Hostname,
            Status = status,
            ExpectedAddress = expectedAddress,
            StatusMessage = "seeded"
        };

        db.ProviderCredentials.Add(credential);
        db.Projects.Add(project);
        db.DeployTargets.Add(target);
        db.ProjectDomains.Add(domain);
        db.SaveChanges();

        var dns = new Mock<IDnsResolver>();
        var certificates = new Mock<ICertificateInspector>();
        var assignment = new Mock<IApplicationDomainAssignment>();
        assignment.SetupGet(a => a.ProviderName).Returns("coolify");
        assignment
            .Setup(a => a.ReadAssignedDomainsAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AssignedDomainRead.Unavailable);

        var assignments = new Mock<IApplicationDomainAssignmentFactory>();
        assignments.Setup(f => f.GetDomainAssignment("coolify")).Returns(assignment.Object);

        var addresses = new Mock<IServerAddressProvider>();
        addresses
            .Setup(a => a.TryGetServerAddressAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServerIp);
        var addressFactory = new Mock<IServerAddressProviderFactory>();
        addressFactory.Setup(f => f.GetServerAddressProvider("coolify")).Returns(addresses.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens
            .Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var orchestrator = new Mock<IDeploymentOrchestrator>();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));

        return new Harness
        {
            Db = db,
            Dns = dns,
            Certificates = certificates,
            Assignment = assignment,
            Orchestrator = orchestrator,
            Clock = clock,
            DomainId = domain.Id,
            Service = new DomainService(
                db,
                dns.Object,
                certificates.Object,
                addressFactory.Object,
                assignments.Object,
                Mock.Of<IDnsZoneProviderFactory>(),
                tokens.Object,
                orchestrator.Object,
                Mock.Of<IDomainReconciliationScheduler>(),
                Microsoft.Extensions.Options.Options.Create(new AppOptions()),
                clock,
                NullLogger<DomainService>.Instance)
        };
    }

    private static DnsCheckResult DnsPointingAt(string address) =>
        DnsObservationCombiner.Combine(
            Hostname, ServerIp, [new DnsObservation("1.1.1.1", true, [address])]);

    private static DnsCheckResult DnsUnanswered() =>
        DnsObservationCombiner.Combine(
            Hostname, ServerIp, [DnsObservation.Unreachable("1.1.1.1", "timed out")]);

    private static CertificateInspection Certificate(CertificateOutcome outcome) =>
        new(Hostname, outcome, "issuer", "subject", null, DateTimeOffset.UtcNow.AddDays(89), [], ["finding"]);

    // The single most important assertion in this feature.
    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("104.16.132.229")]
    public async Task ReconcileOnceAsync_NeverAssignsTheDomain_WhileDnsPointsElsewhere(string observed)
    {
        var harness = CreateHarness(DomainStatus.DnsPending);
        harness.Dns
            .Setup(d => d.CheckAsync(Hostname, ServerIp, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DnsPointingAt(observed));

        await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        harness.Assignment.Verify(a => a.AssignApplicationDomainAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(DomainStatus.DnsPending, harness.Domain.Status);
    }

    [Fact]
    public async Task ReconcileOnceAsync_NeverAssignsTheDomain_WhenDnsCouldNotBeChecked()
    {
        var harness = CreateHarness(DomainStatus.DnsPending);
        harness.Dns
            .Setup(d => d.CheckAsync(Hostname, ServerIp, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DnsUnanswered());

        await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        harness.Assignment.Verify(a => a.AssignApplicationDomainAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReconcileOnceAsync_AdvancesToVerified_WhenDnsReachesTheServer()
    {
        var harness = CreateHarness(DomainStatus.DnsPending);
        harness.Dns
            .Setup(d => d.CheckAsync(Hostname, ServerIp, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DnsPointingAt(ServerIp));

        var result = await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        Assert.Equal(DomainStatus.DnsVerified, result.Status);
        Assert.True(result.ShouldContinue);
    }

    // The scheme is the request for a certificate, so it is only ever written once DNS has earned
    // it -- and it must be https, or the certificate is never requested at all.
    [Fact]
    public async Task ReconcileOnceAsync_WritesTheHttpsScheme_OnceDnsIsVerified()
    {
        var harness = CreateHarness(DomainStatus.DnsVerified);

        await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        harness.Assignment.Verify(a => a.AssignApplicationDomainAsync(
                It.IsAny<ProviderCredentials>(),
                "app-compose",
                $"https://{Hostname}",
                "web",
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(DomainStatus.Assigned, harness.Domain.Status);
    }

    // Coolify has shipped a version where this PATCH returned success without persisting.
    [Fact]
    public async Task ReconcileOnceAsync_StaysUnassigned_WhenTheReadBackShowsTheDomainDidNotStick()
    {
        var harness = CreateHarness(DomainStatus.DnsVerified);
        harness.Assignment
            .Setup(a => a.ReadAssignedDomainsAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssignedDomainRead(true, []));

        var result = await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        Assert.Equal(DomainStatus.DnsVerified, result.Status);
        Assert.True(result.ShouldContinue);
    }

    // A read-back that could not run is not a read-back that found nothing. Treating the two the
    // same would stall every domain on a provider whose response shape we cannot parse.
    [Fact]
    public async Task ReconcileOnceAsync_ProceedsAnyway_WhenTheReadBackCouldNotHappen()
    {
        var harness = CreateHarness(DomainStatus.DnsVerified);
        harness.Assignment
            .Setup(a => a.ReadAssignedDomainsAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AssignedDomainRead.Unavailable);

        var result = await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        Assert.Equal(DomainStatus.Assigned, result.Status);
    }

    // The proxy only routes a newly attached domain after a deploy, and that deploy re-runs the
    // post-deploy assignment -- which would wake this reconciler into triggering another.
    [Fact]
    public async Task ReconcileOnceAsync_TriggersTheRoutingDeployOnlyOnce()
    {
        var harness = CreateHarness(DomainStatus.Assigned);

        await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        var domain = harness.Domain;
        domain.Status = DomainStatus.Assigned;
        harness.Db.SaveChanges();

        await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        harness.Orchestrator.Verify(o => o.TriggerTargetAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReconcileOnceAsync_GoesActive_WhenARealCertificateIsServed()
    {
        var harness = CreateHarness(DomainStatus.CertificatePending);
        harness.Certificates
            .Setup(c => c.InspectAsync(Hostname, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Certificate(CertificateOutcome.Valid));

        var result = await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        Assert.Equal(DomainStatus.Active, result.Status);
        Assert.False(result.ShouldContinue);
    }

    [Fact]
    public async Task ReconcileOnceAsync_KeepsWaiting_WhileTheProxyStillServesItsFallbackCertificate()
    {
        var harness = CreateHarness(DomainStatus.CertificatePending);
        harness.Certificates
            .Setup(c => c.InspectAsync(Hostname, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Certificate(CertificateOutcome.ProxyDefault));

        var result = await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        Assert.Equal(DomainStatus.CertificatePending, result.Status);
        Assert.True(result.ShouldContinue);
    }

    // A step that threw is a step that did not happen. Failing the domain on it would report a
    // transient provider error as the user's DNS being wrong.
    [Fact]
    public async Task ReconcileOnceAsync_KeepsTheDomainAlive_WhenACheckThrows()
    {
        var harness = CreateHarness(DomainStatus.DnsPending);
        harness.Dns
            .Setup(d => d.CheckAsync(Hostname, ServerIp, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("resolver exploded"));

        var result = await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        Assert.Equal(DomainStatus.DnsPending, result.Status);
        Assert.True(result.ShouldContinue);
        Assert.DoesNotContain("exploded", harness.Domain.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileOnceAsync_ReadsTheServerAddress_BeforeAskingWhereTheDomainPoints()
    {
        var harness = CreateHarness(DomainStatus.Pending, expectedAddress: null);

        var result = await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        Assert.Equal(DomainStatus.DnsPending, result.Status);
        Assert.Equal(ServerIp, harness.Domain.ExpectedAddress);
        Assert.NotNull(harness.Domain.DeadlineAt);
    }

    [Fact]
    public async Task ReconcileOnceAsync_DoesNothing_ForADomainThatHasAlreadyFinished()
    {
        var harness = CreateHarness(DomainStatus.Active);

        var result = await harness.Service.ReconcileOnceAsync(harness.DomainId, CancellationToken.None);

        Assert.False(result.ShouldContinue);
        harness.Dns.Verify(d => d.CheckAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AttachAsync_RejectsADomainThatCouldNeverWork()
    {
        var harness = CreateHarness(DomainStatus.Pending);
        var target = harness.Db.DeployTargets.First();

        var ex = await Assert.ThrowsAsync<DeployAIException>(() => harness.Service.AttachAsync(
            harness.Db.Projects.First().UserId, target.ProjectId, target.Id, "*.example.com",
            CancellationToken.None));

        Assert.Equal("domain_invalid", ex.ErrorCode);
    }

    [Fact]
    public async Task AttachAsync_StoresTheNormalizedNameAndWhatTheUserTyped()
    {
        var harness = CreateHarness(DomainStatus.Pending);
        var target = harness.Db.DeployTargets.First();

        var view = await harness.Service.AttachAsync(
            harness.Db.Projects.First().UserId, target.ProjectId, target.Id,
            "  HTTPS://Shop.Example.COM/cart  ", CancellationToken.None);

        Assert.Equal("shop.example.com", view.Hostname);
        Assert.Equal("HTTPS://Shop.Example.COM/cart", view.DisplayHostname);
        Assert.Equal(DomainStatus.Pending, view.Status);
    }
}
