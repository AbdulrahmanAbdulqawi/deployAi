using DeployAI.Api.Services;
using DeployAI.Core.Deployments;

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

    /// <summary>
    /// The allowlist and the readiness gate have to agree. A path the gate calls Blocking-when-missing
    /// and the allowlist refuses to write is a deployment that can never become ready: the generator
    /// produces the file, <c>ParseFilesResponse</c> drops it silently for its extension, and the next
    /// scan reports the same Blocking finding — with nothing anywhere saying why.
    ///
    /// That is not hypothetical. <c>nginx.conf</c> is the file that makes a compose app single-origin,
    /// and <c>.conf</c> was not on the list, so the AI generator could never deliver it. Only the
    /// template path — which does not go through these rules — could, which is why it went unnoticed.
    /// </summary>
    [Fact]
    public void IsAllowedPath_AllowsEveryFileTheComposeGateRequires()
    {
        var website = new DeploymentPlanPart("website", "coolify") { RootDirectory = "client" };
        var server = new DeploymentPlanPart("server", "coolify") { RootDirectory = "server" };

        var refused = SingleOriginComposeReadinessEvaluator
            .BuildReadinessFilePaths(website, server)
            .Where(path => !GeneratedDeploymentFilePathRules.IsAllowedPath(path))
            .ToList();

        Assert.True(
            refused.Count == 0,
            "The compose gate requires files no generator is allowed to write:\n  " + string.Join("\n  ", refused));
    }
}
