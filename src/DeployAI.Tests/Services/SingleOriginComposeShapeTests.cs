using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

/// <summary>
/// Which stacks deploy as one compose resource behind one origin.
///
/// This used to be Angular and nothing else, because "the shape" meant "the stack we have compose
/// templates for". The files now come from the deployment graph, which asks an adapter what a
/// directory is and contains no framework name, so the question is the real one again: is the
/// front end a bundle nginx can serve.
/// </summary>
public class SingleOriginComposeShapeTests
{
    [Theory]
    [InlineData("angular")]
    [InlineData("vite")]
    [InlineData("react")]
    [InlineData("vue")]
    [InlineData("svelte")]
    public void AStaticBundleInFrontOfADotnetServer_IsASingleOriginComposeStack(string websiteFramework)
    {
        Assert.True(SingleOriginComposeShape.Supports(websiteFramework, "dotnet", "coolify", "coolify"));
    }

    /// <summary>
    /// A framework that ships its own server runtime is not a bundle. Fronting it with nginx drops
    /// the half of the app that renders, so these stay on the split-origin path where the server
    /// gets deployed as a server.
    /// </summary>
    [Theory]
    [InlineData("next")]
    [InlineData("nextjs")]
    [InlineData("nuxt")]
    [InlineData("sveltekit")]
    [InlineData("astro")]
    [InlineData("remix")]
    public void AFrameworkWithItsOwnServer_IsNot(string websiteFramework)
    {
        Assert.False(SingleOriginComposeShape.Supports(websiteFramework, "dotnet", "coolify", "coolify"));
    }

    /// <summary>
    /// One compose file can only host both halves if both halves land on the same Coolify server.
    /// </summary>
    [Theory]
    [InlineData("vercel", "railway")]
    [InlineData("vercel", "coolify")]
    [InlineData("coolify", "railway")]
    public void HalvesOnDifferentProviders_AreNot(string websiteProvider, string serverProvider)
    {
        Assert.False(SingleOriginComposeShape.Supports("vite", "dotnet", websiteProvider, serverProvider));
    }
}
