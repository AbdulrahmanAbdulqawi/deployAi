using DeployAI.Api.Services;
using DeployAI.Core.Domains;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// The guards around spending money.
/// </summary>
/// <remarks>
/// Purchase takes a quote id and never an amount, so nothing can ask to be charged a figure the
/// user was not shown. These tests exist because that is the one mistake here whose cost is not
/// measured in debugging time.
/// </remarks>
public class DomainPurchaseServiceTests
{
    private const string Hostname = "yemenconnect.com";
    private const int FirstYear = 1108;
    private const int Renewal = 1108;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness
    {
        public required DeployAIDbContext Db { get; init; }
        public required DomainPurchaseService Service { get; init; }
        public required Mock<IDomainRegistrar> Registrar { get; init; }
        public required Mock<IDomainService> Domains { get; init; }
        public required FixedClock Clock { get; init; }
        public required Guid UserId { get; init; }
        public required Guid ProjectId { get; init; }
        public required Guid DeployTargetId { get; init; }
        public required Guid CredentialId { get; init; }
    }

    private static Harness CreateHarness()
    {
        var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var userId = Guid.NewGuid();
        var credential = new ProviderCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProviderName = "porkbun",
            Kind = CredentialKind.Dns,
            Label = "Default",
            TokenEncrypted = System.Text.Encoding.UTF8.GetBytes(
                PorkbunCredentialStorage.Serialize("pk1_live_abc", "sk1_live_def")),
            IsValid = true
        };
        var project = new Project
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "yemenConnect",
            GitHubRepoFullName = "owner/yemenConnect", DefaultBranch = "main"
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

        var registrar = new Mock<IDomainRegistrar>();
        registrar.SetupGet(r => r.ProviderName).Returns("porkbun");
        registrar.SetupGet(r => r.DisplayName).Returns("Porkbun");
        registrar
            .Setup(r => r.CheckAvailabilityAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderCredentials _, string h, CancellationToken _) => new DomainOffer(
                h, DomainAvailability.Available,
                new DomainPrice(FirstYear, Renewal, false, false, 1),
                $"{h} is available."));
        registrar
            .Setup(r => r.DryRunAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderCredentials _, string h, int c, CancellationToken _) =>
                new DomainRegistration(true, h, null, c, "Would succeed."));
        registrar
            .Setup(r => r.RegisterAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderCredentials _, string h, int c, string _, CancellationToken _) =>
                new DomainRegistration(true, h, "9912355", c, $"{h} is yours."));

        var registrars = new Mock<IDomainRegistrarFactory>();
        registrars.Setup(f => f.GetRegistrar("porkbun")).Returns(registrar.Object);
        registrars.SetupGet(f => f.All).Returns([registrar.Object]);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens
            .Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PorkbunCredentialStorage.Serialize("pk1_live_abc", "sk1_live_def"));

        var domains = new Mock<IDomainService>();
        domains
            .Setup(d => d.AttachAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectDomainView(
                Guid.NewGuid(), target.Id, Hostname, Hostname, DomainSource.Registrar,
                DomainStatus.Pending, true, "Getting ready.", null, null, null, null, null));

        var clock = new FixedClock(DateTimeOffset.Parse("2026-08-17T10:00:00Z"));

        return new Harness
        {
            Db = db,
            Registrar = registrar,
            Domains = domains,
            Clock = clock,
            UserId = userId,
            ProjectId = project.Id,
            DeployTargetId = target.Id,
            CredentialId = credential.Id,
            Service = new DomainPurchaseService(
                db, registrars.Object, tokens.Object, domains.Object, clock,
                NullLogger<DomainPurchaseService>.Instance)
        };
    }

    private static async Task<Guid> QuoteAsync(Harness harness)
    {
        var results = await harness.Service.SearchAsync(
            harness.UserId, Hostname, harness.ProjectId, harness.DeployTargetId, CancellationToken.None);
        return results.Single().QuoteId!.Value;
    }

    // ---- searching -------------------------------------------------------

    [Fact]
    public async Task SearchAsync_QuotesBothPrices_NotJustTheFirstYear()
    {
        var harness = CreateHarness();
        harness.Registrar
            .Setup(r => r.CheckAvailabilityAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainOffer(
                Hostname, DomainAvailability.Available,
                new DomainPrice(199, 3499, IsFirstYearPromotional: true, IsPremium: false, 1),
                "cheap now, dear later"));

        var result = (await harness.Service.SearchAsync(
            harness.UserId, Hostname, null, null, CancellationToken.None)).Single();

        Assert.Equal(199, result.FirstYearCents);
        Assert.Equal(3499, result.RenewalCents);
        Assert.True(result.IsFirstYearPromotional);
    }

    [Fact]
    public async Task SearchAsync_WritesTheQuoteDown_SoThePriceIsTheServersWord()
    {
        var harness = CreateHarness();

        var quoteId = await QuoteAsync(harness);

        var stored = harness.Db.DomainPurchases.Single();
        Assert.Equal(quoteId, stored.Id);
        Assert.Equal(DomainPurchaseStatus.Quoted, stored.Status);
        Assert.Equal(FirstYear, stored.FirstYearCents);
        Assert.Equal(Hostname, stored.Hostname);
    }

    [Fact]
    public async Task SearchAsync_NormalisesWhatWasTyped()
    {
        var harness = CreateHarness();

        var result = (await harness.Service.SearchAsync(
            harness.UserId, "  HTTPS://YemenConnect.COM/  ", null, null, CancellationToken.None)).Single();

        Assert.Equal(Hostname, result.Hostname);
    }

    [Fact]
    public async Task SearchAsync_QuotesNothingForAnUnavailableDomain()
    {
        var harness = CreateHarness();
        harness.Registrar
            .Setup(r => r.CheckAvailabilityAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainOffer(Hostname, DomainAvailability.Taken, null, "taken"));

        var result = (await harness.Service.SearchAsync(
            harness.UserId, Hostname, null, null, CancellationToken.None)).Single();

        Assert.Null(result.QuoteId);
        Assert.Empty(harness.Db.DomainPurchases);
    }

    // Cloudflare hosts DNS and cannot register, so "connect a DNS account" is not the same as
    // "you can buy here".
    [Fact]
    public async Task SearchAsync_SaysSoWhenNoConnectedAccountCanBuy()
    {
        var harness = CreateHarness();
        harness.Db.ProviderCredentials.RemoveRange(harness.Db.ProviderCredentials);
        harness.Db.SaveChanges();

        var ex = await Assert.ThrowsAsync<DeployAIException>(() => harness.Service.SearchAsync(
            harness.UserId, Hostname, null, null, CancellationToken.None));

        Assert.Equal("domain_registrar_not_connected", ex.ErrorCode);
    }

    // ---- the money guards ------------------------------------------------

    [Fact]
    public async Task PurchaseAsync_BuysAtTheQuotedPrice_AndNothingElse()
    {
        var harness = CreateHarness();
        var quoteId = await QuoteAsync(harness);

        var result = await harness.Service.PurchaseAsync(
            harness.UserId, quoteId, agreeToTerms: true, CancellationToken.None);

        Assert.True(result.Succeeded);
        harness.Registrar.Verify(r => r.RegisterAsync(
            It.IsAny<ProviderCredentials>(), Hostname, FirstYear,
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // The registrar refuses without it, but DeployAI asks in its own right rather than sending
    // "yes" on someone's behalf.
    [Fact]
    public async Task PurchaseAsync_RefusesWithoutAgreementToTheRegistrationTerms()
    {
        var harness = CreateHarness();
        var quoteId = await QuoteAsync(harness);

        var ex = await Assert.ThrowsAsync<DeployAIException>(() => harness.Service.PurchaseAsync(
            harness.UserId, quoteId, agreeToTerms: false, CancellationToken.None));

        Assert.Equal("domain_terms_not_agreed", ex.ErrorCode);
        harness.Registrar.Verify(r => r.RegisterAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PurchaseAsync_RefusesAQuoteThatHasExpired()
    {
        var harness = CreateHarness();
        var quoteId = await QuoteAsync(harness);
        harness.Clock.Now = harness.Clock.Now.AddHours(1);

        var ex = await Assert.ThrowsAsync<DeployAIException>(() => harness.Service.PurchaseAsync(
            harness.UserId, quoteId, agreeToTerms: true, CancellationToken.None));

        Assert.Equal("domain_quote_expired", ex.ErrorCode);
        harness.Registrar.Verify(r => r.RegisterAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A double-submit must not buy twice. The honest answer is what already happened.
    [Fact]
    public async Task PurchaseAsync_BuysOnce_WhenTheSameQuoteIsSubmittedTwice()
    {
        var harness = CreateHarness();
        var quoteId = await QuoteAsync(harness);

        var first = await harness.Service.PurchaseAsync(
            harness.UserId, quoteId, true, CancellationToken.None);
        var second = await harness.Service.PurchaseAsync(
            harness.UserId, quoteId, true, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Contains("already bought", second.Message, StringComparison.OrdinalIgnoreCase);
        harness.Registrar.Verify(r => r.RegisterAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Belt and braces: the quote id is the idempotency key, so even a retry that got past the
    // status check could not charge twice at the registrar.
    [Fact]
    public async Task PurchaseAsync_UsesTheQuoteIdAsTheIdempotencyKey()
    {
        var harness = CreateHarness();
        var quoteId = await QuoteAsync(harness);

        await harness.Service.PurchaseAsync(harness.UserId, quoteId, true, CancellationToken.None);

        harness.Registrar.Verify(r => r.RegisterAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<int>(),
            quoteId.ToString(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Re-priced immediately before buying, so a price that moved fails here rather than becoming a
    // surprise on a statement.
    [Fact]
    public async Task PurchaseAsync_StopsWhenThePriceHasMovedSinceTheQuote()
    {
        var harness = CreateHarness();
        var quoteId = await QuoteAsync(harness);
        harness.Registrar
            .Setup(r => r.DryRunAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainRegistration(
                false, Hostname, null, null,
                "The cost submitted must equal the cost of the domain for its minimum allowed duration."));

        var result = await harness.Service.PurchaseAsync(
            harness.UserId, quoteId, true, CancellationToken.None);

        Assert.False(result.Succeeded);
        harness.Registrar.Verify(r => r.RegisterAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(DomainPurchaseStatus.Failed, harness.Db.DomainPurchases.Single().Status);
    }

    [Fact]
    public async Task PurchaseAsync_RefusesAQuoteBelongingToSomebodyElse()
    {
        var harness = CreateHarness();
        var quoteId = await QuoteAsync(harness);

        var ex = await Assert.ThrowsAsync<DeployAIException>(() => harness.Service.PurchaseAsync(
            Guid.NewGuid(), quoteId, true, CancellationToken.None));

        Assert.Equal("domain_quote_not_found", ex.ErrorCode);
    }

    [Fact]
    public async Task PurchaseAsync_RecordsWhatWasActuallyCharged()
    {
        var harness = CreateHarness();
        var quoteId = await QuoteAsync(harness);

        await harness.Service.PurchaseAsync(harness.UserId, quoteId, true, CancellationToken.None);

        var receipt = harness.Db.DomainPurchases.AsNoTracking().Single();
        Assert.Equal(DomainPurchaseStatus.Completed, receipt.Status);
        Assert.Equal(FirstYear, receipt.ChargedCents);
        Assert.Equal("9912355", receipt.OrderId);
        Assert.NotNull(receipt.CompletedAt);
    }

    // ---- handing over to the reconciler ----------------------------------

    [Fact]
    public async Task PurchaseAsync_HandsTheBoughtDomainToTheReconciler()
    {
        var harness = CreateHarness();
        var quoteId = await QuoteAsync(harness);

        var result = await harness.Service.PurchaseAsync(
            harness.UserId, quoteId, true, CancellationToken.None);

        Assert.NotNull(result.DomainId);
        harness.Domains.Verify(d => d.AttachAsync(
            harness.UserId, harness.ProjectId, harness.DeployTargetId, Hostname,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // The domain is bought and paid for whatever happens next. Reporting a wiring failure as a
    // failed purchase would have the user try again and buy nothing, having already been charged.
    [Fact]
    public async Task PurchaseAsync_StillSucceeds_WhenWiringTheDomainUpFails()
    {
        var harness = CreateHarness();
        harness.Domains
            .Setup(d => d.AttachAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("target vanished"));
        var quoteId = await QuoteAsync(harness);

        var result = await harness.Service.PurchaseAsync(
            harness.UserId, quoteId, true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.DomainId);
        Assert.Equal(DomainPurchaseStatus.Completed, harness.Db.DomainPurchases.Single().Status);
    }

    [Fact]
    public async Task PurchaseAsync_DoesNotAttach_WhenTheSearchWasNotForAProject()
    {
        var harness = CreateHarness();
        var results = await harness.Service.SearchAsync(
            harness.UserId, Hostname, projectId: null, deployTargetId: null, CancellationToken.None);

        var result = await harness.Service.PurchaseAsync(
            harness.UserId, results.Single().QuoteId!.Value, true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.DomainId);
        harness.Domains.Verify(d => d.AttachAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
