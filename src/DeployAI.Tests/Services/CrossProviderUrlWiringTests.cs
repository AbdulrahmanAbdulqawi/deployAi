using DeployAI.Api.Services;

namespace DeployAI.Tests.Services;

public class CrossProviderUrlWiringTests
{
    [Theory]
    [InlineData("angular", true)]
    [InlineData("vite", false)]
    [InlineData("react", false)]
    [InlineData(null, true)]
    public void UsesRelativeApiPaths_MatchesFramework(string? framework, bool expected)
    {
        Assert.Equal(expected, CrossProviderUrlWiring.UsesRelativeApiPaths(framework));
    }

    [Fact]
    public void ResolveApiEnvKeys_IncludesAngularKey()
    {
        var keys = CrossProviderUrlWiring.ResolveApiEnvKeys("angular");
        Assert.Contains("NG_APP_API_URL", keys);
        Assert.Contains("API_URL", keys);
    }

    [Fact]
    public void ResolveServerCorsEnvKeys_IncludesDotnetKeys()
    {
        var keys = CrossProviderUrlWiring.ResolveServerCorsEnvKeys("dotnet");
        Assert.Contains("App__FrontendUrl", keys);
    }

    [Fact]
    public void NormalizeOrigin_AddsHttpsAndTrimsSlash()
    {
        Assert.Equal("https://api.example.com", CrossProviderUrlWiring.NormalizeOrigin("api.example.com/"));
    }
}
