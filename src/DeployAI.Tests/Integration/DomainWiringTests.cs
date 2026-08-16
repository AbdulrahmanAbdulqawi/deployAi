using DeployAI.Api.Services;
using DeployAI.Core.Domains;
using DeployAI.Core.Providers;
using DeployAI.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DeployAI.Tests.Integration;

/// <summary>
/// Proves the domain feature is actually wired into the running application, not merely compiled.
/// </summary>
/// <remarks>
/// Controllers are constructed lazily, so a service registered with a missing dependency stays
/// invisible to every other test in the suite and surfaces as a 500 the first time a real user
/// opens the page. Most incidents in this codebase have been wiring rather than logic, which is
/// exactly the class of failure this catches.
/// </remarks>
public class DomainWiringTests : IClassFixture<DeployAIWebApplicationFactory>
{
    private readonly DeployAIWebApplicationFactory _factory;

    public DomainWiringTests(DeployAIWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void DomainService_ResolvesWithEveryDependencyItNeeds()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDomainService>());
    }

    [Fact]
    public void ReconciliationJob_Resolves_SoHangfireCanRunIt()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DomainReconciliationJob>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDomainReconciliationScheduler>());
    }

    [Fact]
    public void TheTwoProbes_Resolve()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDnsResolver>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICertificateInspector>());
    }

    // A capability registered but absent from its factory resolves fine on its own and returns
    // null at the one call site that needs it.
    [Fact]
    public void CoolifyIsReachable_ThroughEveryDomainCapabilityFactory()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<IServerAddressProviderFactory>()
            .GetServerAddressProvider("coolify"));

        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<IApplicationDomainAssignmentFactory>()
            .GetDomainAssignment("coolify"));
    }

    [Fact]
    public void CloudflareIsReachable_ThroughTheDnsZoneFactory()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<IDnsZoneProviderFactory>()
            .GetZoneProvider("cloudflare"));
    }

    // The API has no global enum-to-string converter, so an un-annotated enum crosses the wire as
    // a number. The client switches on string values, so every status fell through to its default
    // branch and a domain waiting on DNS was labelled "Removed" — with its record instructions
    // hidden, because the source comparison missed too. Found by opening the page; invisible to
    // every test that built its own view objects.
    [Fact]
    public void DomainEnums_SerializeAsStrings_BecauseTheClientSwitchesOnNames()
    {
        var view = new ProjectDomainView(
            Guid.NewGuid(), Guid.NewGuid(), "app.example.com", "app.example.com",
            DomainSource.UserProvided, DomainStatus.DnsPending, true, "waiting",
            "46.225.80.188", null, null, null, null);

        var json = System.Text.Json.JsonSerializer.Serialize(
            view, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"status\":\"DnsPending\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"UserProvided\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DnsController_ResolvesWithEveryDependencyItNeeds()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(ActivatorUtilities.CreateInstance<DeployAI.Api.Controllers.DnsController>(
            scope.ServiceProvider));
    }

    // Same reason as the domain enums above: nothing configures enum-as-string globally, and the
    // client groups zones by this value. Shipped as an integer it would match no case at all.
    [Fact]
    public void DnsZoneUsability_SerializesAsAString()
    {
        var zone = new DnsZone("z", "example.com", null, DnsZoneUsability.NotDelegated, "waiting");

        var json = System.Text.Json.JsonSerializer.Serialize(
            zone, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"usability\":\"NotDelegated\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DnsCredentialVerdict_SerializesAsAString()
    {
        var check = new DnsCredentialCheck(DnsCredentialVerdict.CannotListZones, "nope", []);

        var json = System.Text.Json.JsonSerializer.Serialize(
            check, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"verdict\":\"CannotListZones\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownProvider_ReturnsNull_RatherThanThrowing()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider
            .GetRequiredService<IDnsZoneProviderFactory>()
            .GetZoneProvider("not-a-provider"));
    }
}
