using DeployAI.Api.Services;
using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

public class ObjectStorageEnvironmentWiringTests
{
    private static readonly ObjectStorageConnection Complete = new(
        "https://fsn1.your-objectstorage.com",
        "fsn1",
        "yemeni-breeze",
        "AK123",
        "SK456");

    [Fact]
    public void BuildAssignments_ForDotnet_UsesConfigurationBindingKeys()
    {
        var assignments = ObjectStorageEnvironmentWiring
            .BuildAssignments("dotnet", Complete)
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);

        Assert.Equal("https://fsn1.your-objectstorage.com", assignments["Storage__Endpoint"]);
        Assert.Equal("fsn1", assignments["Storage__Region"]);
        Assert.Equal("yemeni-breeze", assignments["Storage__Bucket"]);
        Assert.Equal("AK123", assignments["Storage__AccessKey"]);
        Assert.Equal("SK456", assignments["Storage__SecretKey"]);
    }

    // A generically-"docker" .NET service must land on the same keys as an explicitly-.NET one,
    // or the app reads nothing and silently falls back to local disk.
    [Fact]
    public void BuildAssignments_ForDockerFramework_StillUsesDotnetKeys()
    {
        var assignments = ObjectStorageEnvironmentWiring.BuildAssignments("docker", Complete);

        Assert.Contains(assignments, a => a.Key == "Storage__Bucket");
    }

    [Fact]
    public void BuildAssignments_ForNode_UsesFlatS3Keys()
    {
        var assignments = ObjectStorageEnvironmentWiring
            .BuildAssignments("node", Complete)
            .Select(a => a.Key)
            .ToList();

        Assert.Contains("S3_ENDPOINT", assignments);
        Assert.Contains("S3_BUCKET", assignments);
        Assert.DoesNotContain("Storage__Bucket", assignments);
    }

    // Access keys must not be readable back out of the deployment platform's UI.
    [Fact]
    public void BuildAssignments_MarksOnlyTheKeysAsSecret()
    {
        var assignments = ObjectStorageEnvironmentWiring.BuildAssignments("dotnet", Complete);

        Assert.True(assignments.Single(a => a.Key == "Storage__AccessKey").IsSecret);
        Assert.True(assignments.Single(a => a.Key == "Storage__SecretKey").IsSecret);
        Assert.False(assignments.Single(a => a.Key == "Storage__Bucket").IsSecret);
        Assert.False(assignments.Single(a => a.Key == "Storage__Endpoint").IsSecret);
    }

    // The fallback contract is the dangerous part: a blank value doesn't fail, it downgrades
    // the app to a container filesystem that disappears on the next redeploy.
    [Theory]
    [InlineData("", "fsn1", "bucket", "ak", "sk")]
    [InlineData("https://endpoint", "fsn1", "", "ak", "sk")]
    [InlineData("https://endpoint", "fsn1", "bucket", "", "sk")]
    [InlineData("https://endpoint", "fsn1", "bucket", "ak", "")]
    public void IsComplete_IsFalse_WhenAnyRequiredValueIsBlank(
        string endpoint, string region, string bucket, string accessKey, string secretKey)
    {
        var connection = new ObjectStorageConnection(endpoint, region, bucket, accessKey, secretKey);

        Assert.False(ObjectStorageEnvironmentWiring.IsComplete(connection));
    }

    // Region signs the request but isn't part of the S3-vs-disk switch, so a blank one is
    // still a "complete" connection as far as the fallback contract is concerned.
    [Fact]
    public void IsComplete_IgnoresRegion()
    {
        var connection = Complete with { Region = "" };

        Assert.True(ObjectStorageEnvironmentWiring.IsComplete(connection));
    }

    [Fact]
    public void BuildAssignments_RefusesToWriteAnIncompleteConnection()
    {
        var connection = Complete with { SecretKey = "" };

        Assert.Throws<ArgumentException>(() =>
            ObjectStorageEnvironmentWiring.BuildAssignments("dotnet", connection));
    }

    [Theory]
    [InlineData("Yemeni Breeze", "yemeni-breeze")]
    [InlineData("my_app.v2", "my-app-v2")]
    [InlineData("--leading--and--trailing--", "leading-and-trailing")]
    [InlineData("ab", "app-ab")]
    public void BuildBucketName_ProducesAValidS3Name(string projectName, string expected)
    {
        Assert.Equal(expected, ObjectStorageProvisioningService.BuildBucketName(projectName));
    }

    [Fact]
    public void BuildBucketName_TruncatesToTheS3Limit()
    {
        var name = ObjectStorageProvisioningService.BuildBucketName(new string('a', 100));

        Assert.Equal(63, name.Length);
    }
}
