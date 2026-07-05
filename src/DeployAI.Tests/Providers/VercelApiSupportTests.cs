using DeployAI.Providers.Vercel;

namespace DeployAI.Tests.Providers;

public class VercelApiSupportTests
{
    [Theory]
    [InlineData("client", "dist/client/browser")]
    [InlineData("idaara.client", "dist/idaara.client")]
    [InlineData("apps/web", "dist/web")]
    public void ResolveAngularOutputDirectory_MatchesProjectLayout(string rootDirectory, string expected)
    {
        Assert.Equal(expected, VercelApiSupport.ResolveAngularOutputDirectory(rootDirectory));
    }
}
