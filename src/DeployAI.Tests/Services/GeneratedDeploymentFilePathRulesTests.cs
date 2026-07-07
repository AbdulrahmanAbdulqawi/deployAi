using DeployAI.Api.Services;

namespace DeployAI.Tests.Services;

public class GeneratedDeploymentFilePathRulesTests
{
    [Theory]
    [InlineData("TicketHub.Server/Dockerfile")]
    [InlineData("src/Api/Dockerfile.prod")]
    [InlineData("tickethub.client/tickethub.client.esproj")]
    [InlineData("src/app/app.component.ts")]
    [InlineData("railway.toml")]
    public void IsAllowedPath_AllowsCommonFixPaths(string path)
    {
        Assert.True(GeneratedDeploymentFilePathRules.IsAllowedPath(path));
    }

    [Theory]
    [InlineData("../secrets.env")]
    [InlineData("bin/app.dll")]
    public void IsAllowedPath_RejectsUnsafeOrBinaryPaths(string path)
    {
        Assert.False(GeneratedDeploymentFilePathRules.IsAllowedPath(path));
    }
}
