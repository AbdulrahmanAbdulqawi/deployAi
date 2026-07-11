using DeployAI.Core.Providers;
using DeployAI.Providers;
using DeployAI.Providers.Vercel;
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
}
