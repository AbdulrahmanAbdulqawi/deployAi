using DeployAI.Api.Services;

namespace DeployAI.Tests.Services;

public class DevCorsOriginPolicyTests
{
    [Theory]
    [InlineData("http://localhost:4200")]
    [InlineData("http://localhost:4201")]
    [InlineData("http://localhost:4202")]
    [InlineData("http://localhost:9999")]
    [InlineData("http://127.0.0.1:4200")]
    [InlineData("https://localhost:4200")]
    public void IsLocalDevOrigin_AllowsAnyLocalhostPort(string origin)
    {
        Assert.True(DevCorsOriginPolicy.IsLocalDevOrigin(origin));
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("https://localhost.evil.example.com")]
    [InlineData("http://deployai-mu.vercel.app")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    public void IsLocalDevOrigin_RejectsEverythingElse(string? origin)
    {
        Assert.False(DevCorsOriginPolicy.IsLocalDevOrigin(origin));
    }
}
