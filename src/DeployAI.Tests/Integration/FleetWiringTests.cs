using System.Text.Json.Serialization;
using DeployAI.Api.Controllers;
using DeployAI.Api.Services;
using DeployAI.Api.Services.Checks;
using DeployAI.Core.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DeployAI.Tests.Integration;

/// <summary>
/// Proves fleet verification is actually wired into the running application, not merely compiled.
/// </summary>
/// <remarks>
/// The sweep runs on a schedule with nobody watching, so a missing registration would not surface as
/// a failed request — it would surface as a monitor that quietly never ran, which is the exact
/// failure the whole feature exists to prevent. Most incidents in this codebase have been wiring.
/// </remarks>
public class FleetWiringTests : IClassFixture<DeployAIWebApplicationFactory>
{
    private readonly DeployAIWebApplicationFactory _factory;

    public FleetWiringTests(DeployAIWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void FleetVerificationService_ResolvesWithEveryDependencyItNeeds()
    {
        Assert.NotNull(_factory.Services.GetRequiredService<IFleetVerificationService>());
    }

    [Fact]
    public void TheSweepsScopedCollaborators_Resolve()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IProjectSweepRunner>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IProjectVerificationService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IProjectVerificationRecorder>());
    }

    [Fact]
    public void BothRecurringJobs_Resolve_SoHangfireCanRunThem()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProjectHealthMonitorJob>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<EnvironmentDriftCheckJob>());
    }

    [Fact]
    public void FleetController_ResolvesWithEveryDependencyItNeeds()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(ActivatorUtilities.CreateInstance<FleetController>(scope.ServiceProvider));
    }

    /// <summary>
    /// Every family of checks is registered, so none of them silently stops contributing.
    /// </summary>
    /// <remarks>
    /// A contributor that is written but never registered produces no error anywhere: the sweep runs,
    /// the project reports healthy, and the check it was supposed to make simply never happens. The
    /// count is asserted rather than the list, so adding a contributor without registering it fails
    /// here rather than going unnoticed.
    /// </remarks>
    [Fact]
    public void EveryCheckContributor_IsRegistered()
    {
        using var scope = _factory.Services.CreateScope();

        var contributors = scope.ServiceProvider.GetServices<IProjectCheckContributor>().ToList();
        var names = contributors.Select(c => c.Name).ToList();

        Assert.Contains("live URLs", names);
        Assert.Contains("provider", names);
        Assert.Contains("connections", names);
        Assert.Contains("runtime", names);
        Assert.Contains("domains", names);
        Assert.Contains("configuration", names);
    }

    /// <summary>
    /// Every provider that can deploy can also be asked whether what it deployed still exists.
    /// </summary>
    [Fact]
    public void ApplicationExistence_IsResolvableForEveryDeploymentProvider()
    {
        var existence = _factory.Services.GetRequiredService<IProviderApplicationExistenceFactory>();

        foreach (var provider in _factory.Services.GetServices<IDeploymentProvider>())
        {
            Assert.True(
                existence.GetApplicationExistence(provider.ProviderName) is not null,
                $"{provider.ProviderName} can deploy but cannot say whether the app still exists.");
        }
    }

    /// <summary>
    /// Enums ship as their names, as a configured convention rather than a per-enum attribute.
    /// </summary>
    /// <remarks>
    /// The failure this guards is silent in both directions: an enum serialized as an integer reaches
    /// TypeScript as a number, every string comparison misses, and the UI renders whatever its
    /// default branch says. That is how a domain waiting on DNS came out labelled "Removed". Asserting
    /// the converter is configured — rather than asserting one enum's output — is what makes this a
    /// convention: a new enum inherits it without anyone remembering an attribute.
    /// </remarks>
    [Fact]
    public void EnumsAreConfiguredToShipAsStrings_ForEveryController()
    {
        var options = _factory.Services.GetRequiredService<IOptions<JsonOptions>>();

        Assert.Contains(
            options.Value.JsonSerializerOptions.Converters,
            converter => converter is JsonStringEnumConverter);
    }
}
