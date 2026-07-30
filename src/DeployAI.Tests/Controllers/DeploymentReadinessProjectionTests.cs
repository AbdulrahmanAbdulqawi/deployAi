using System.Text.Json;
using DeployAI.Api.Controllers;
using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Controllers;

/// <summary>
/// The readiness scan is projected to JSON by hand, so a field it computes reaches the wizard only
/// if someone remembered to list it. One had not been: <c>usesSingleOriginCompose</c> was resolved
/// on every scan and dropped at the boundary, so a compose repository could have its missing compose
/// file fetched, evaluated and marked blocking — and the wizard, which decides what to show from
/// that flag, rendered nothing and let the deploy proceed.
/// </summary>
public class DeploymentReadinessProjectionTests
{
    [Fact]
    public void TheProjectionSaysWhichShapeTheFindingsBelongTo()
    {
        var json = Project(new DeploymentReadinessResult(
            IsReady: false,
            CommitSha: "23089d0",
            UsesSplitOrigin: false,
            MissingFiles:
            [
                new MissingDeploymentFile(
                    "docker-compose.coolify.yml",
                    "A Docker Compose file is required.",
                    DeploymentFileSeverity.Blocking)
            ],
            Warnings: [],
            UsesSingleOriginCompose: true));

        Assert.True(json.GetProperty("usesSingleOriginCompose").GetBoolean());
        Assert.False(json.GetProperty("usesSplitOrigin").GetBoolean());
        Assert.False(json.GetProperty("isReady").GetBoolean());
    }

    [Fact]
    public void TheProjectionStillCarriesEveryFindingAndItsSeverity()
    {
        var json = Project(new DeploymentReadinessResult(
            IsReady: false,
            CommitSha: null,
            UsesSplitOrigin: true,
            MissingFiles:
            [
                new MissingDeploymentFile("client/nginx.conf", "Missing.", DeploymentFileSeverity.Blocking),
                new MissingDeploymentFile("docs/DEPLOYMENT.md", "Document it.", DeploymentFileSeverity.Recommended)
            ],
            Warnings: ["POST to /api returns 405."]));

        var files = json.GetProperty("missingFiles").EnumerateArray().ToArray();
        Assert.Equal(2, files.Length);
        Assert.Equal("blocking", files[0].GetProperty("severity").GetString());
        Assert.Equal("recommended", files[1].GetProperty("severity").GetString());
        Assert.Single(json.GetProperty("warnings").EnumerateArray());
    }

    private static JsonElement Project(DeploymentReadinessResult result) =>
        JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(DeploymentSetupController.MapReadiness(result)));
}
