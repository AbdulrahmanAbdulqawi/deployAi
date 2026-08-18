using DeployAI.Core.Providers;
using DeployAI.Providers;
using DeployAI.Providers.Vercel;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DeployAI.Tests.Providers;

public class ProviderFactoryTests
{
    [Fact]
    public void GetAvailableProviders_IncludesVercel()
    {
        var vercel = new Mock<IDeploymentProvider>();
        vercel.Setup(p => p.ProviderName).Returns("vercel");
        vercel.Setup(p => p.DisplayName).Returns("Vercel");
        vercel.Setup(p => p.ApiStyle).Returns("rest");

        var factory = new ProviderFactory([vercel.Object]);
        var providers = factory.GetAvailableProviders();

        Assert.Contains(providers, p => p.Name == "vercel" && p.DisplayName == "Vercel");
    }

    [Fact]
    public void GetAvailableProviders_IncludesCoolify()
    {
        var coolify = new Mock<IDeploymentProvider>();
        coolify.Setup(p => p.ProviderName).Returns(ProviderNameValues.Coolify);
        coolify.Setup(p => p.DisplayName).Returns("Coolify");
        coolify.Setup(p => p.ApiStyle).Returns("rest");

        var factory = new ProviderFactory([coolify.Object]);
        var providers = factory.GetAvailableProviders();

        Assert.Contains(providers, p => p.Name == ProviderNameValues.Coolify && p.DisplayName == "Coolify");
    }

    [Fact]
    public void GetProvider_ThrowsForUnknownProvider()
    {
        var factory = new ProviderFactory(Array.Empty<IDeploymentProvider>());
        Assert.Throws<KeyNotFoundException>(() => factory.GetProvider("unknown"));
    }

    /// <summary>
    /// Every provider DeployAI can deploy to must also be able to say whether what it deployed is
    /// still there.
    /// </summary>
    /// <remarks>
    /// A structural guard rather than a note in a document. Without it, "remember to register the
    /// new provider as IProviderApplicationExistence too" is exactly the kind of rule that depends on
    /// someone remembering — and the consequence of forgetting is silent: the new provider's projects
    /// simply never get checked for existence, and go on reporting healthy forever. With it, adding
    /// a provider and not the capability turns the build red.
    /// </remarks>
    [Fact]
    public void EveryDeploymentProvider_AlsoAnswersApplicationExistence()
    {
        var services = new ServiceCollection().AddDeploymentProviders().BuildServiceProvider();

        var deploymentProviders = services.GetServices<IDeploymentProvider>()
            .Select(p => p.ProviderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existenceFactory = services.GetRequiredService<IProviderApplicationExistenceFactory>();

        Assert.NotEmpty(deploymentProviders);
        var missing = deploymentProviders
            .Where(name => existenceFactory.GetApplicationExistence(name) is null)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"These providers can deploy but cannot say whether the app still exists: {string.Join(", ", missing)}.");
    }
}
