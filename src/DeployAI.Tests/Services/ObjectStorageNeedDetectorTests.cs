using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.Services;

/// <summary>
/// Deciding an app stores files has to be earned from the repository, because acting on it creates
/// a billable bucket and writes five variables onto a live app. These pin both directions: the
/// signals that must be caught, and the near-misses that must not buy anyone a bucket.
/// </summary>
public class ObjectStorageNeedDetectorTests
{
    private static readonly ObjectStorageNeedDetector Detector = new();

    [Fact]
    public void Detect_FindsTheNeed_FromADotnetStorageSection()
    {
        // yemenConnect's actual shape. Its Media module binds this section, and with it unset the
        // app writes uploads to a container filesystem that disappears on the next redeploy.
        var need = Detector.Detect(new ObjectStorageScanInputs(AppsettingsContent: """
            {
              "Storage": {
                "Endpoint": "",
                "Region": "us-east-1",
                "Bucket": "yemenhub-media",
                "AccessKey": "",
                "SecretKey": ""
              }
            }
            """));

        Assert.True(need.Needed);
        Assert.Contains(need.Evidence, e => e.Contains("Storage section"));
    }

    [Theory]
    [InlineData("""<PackageReference Include="AWSSDK.S3" Version="3.7.0" />""", "AWSSDK.S3")]
    [InlineData("""{"dependencies":{"@aws-sdk/client-s3":"^3.0.0"}}""", "@aws-sdk/client-s3")]
    [InlineData("boto3==1.34.0", "boto3")]
    [InlineData("""{"dependencies":{"multer-s3":"^3.0.1"}}""", "multer-s3")]
    public void Detect_FindsTheNeed_FromAnS3ClientDependency(string manifest, string expected)
    {
        // The strongest signal there is: nothing pulls in an S3 client by accident.
        var need = Detector.Detect(new ObjectStorageScanInputs(ManifestContents: [manifest]));

        Assert.True(need.Needed);
        Assert.Contains(need.Evidence, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Detect_FindsTheNeed_FromMinioInTheReposOwnComposeFile()
    {
        // A repo that runs MinIO for itself in development needs the real thing in production.
        var need = Detector.Detect(new ObjectStorageScanInputs(ComposeContent: """
            services:
              minio:
                image: minio/minio
            """));

        Assert.True(need.Needed);
        Assert.Contains(need.Evidence, e => e.Contains("MinIO"));
    }

    [Fact]
    public void Detect_SaysNothing_ForAnAppThatStoresNoFiles()
    {
        var need = Detector.Detect(new ObjectStorageScanInputs(
            AppsettingsContent: """{"ConnectionStrings":{"Postgres":"Host=db"},"Jwt":{"SigningKey":"x"}}""",
            ComposeContent: "services:\n  db:\n    image: postgres:16\n",
            ManifestContents: ["""<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />"""]));

        Assert.False(need.Needed);
        Assert.Empty(need.Evidence);
    }

    [Fact]
    public void Detect_IgnoresAStorageSectionThatIsNotAboutBuckets()
    {
        // "Storage" is a common word. A section with no bucket and no credential is a local path
        // or a volume note, and provisioning against it would hand someone a bucket they never
        // asked for and a bill they cannot explain.
        var need = Detector.Detect(new ObjectStorageScanInputs(
            AppsettingsContent: """{"Storage":{"Path":"/var/data","MaxSizeMb":500}}"""));

        Assert.False(need.Needed);
    }

    [Fact]
    public void Detect_ReportsEveryReason_NotJustTheFirst()
    {
        // The deploy log shows these. One line of evidence is checkable; "we decided you need a
        // bucket" is not.
        var need = Detector.Detect(new ObjectStorageScanInputs(
            AppsettingsContent: """{"Storage":{"Bucket":"media","AccessKey":"","SecretKey":""}}""",
            ComposeContent: "services:\n  minio:\n    image: minio/minio\n",
            ManifestContents: ["""<PackageReference Include="AWSSDK.S3" />"""]));

        Assert.True(need.Needed);
        Assert.True(need.Evidence.Count >= 3, $"expected several reasons, got: {string.Join("; ", need.Evidence)}");
    }

    [Fact]
    public void Detect_DoesNotRepeatTheSameReasonTwice()
    {
        var need = Detector.Detect(new ObjectStorageScanInputs(
            ManifestContents: ["""<PackageReference Include="AWSSDK.S3" />""", """<PackageReference Include="AWSSDK.S3" />"""]));

        Assert.Single(need.Evidence);
    }

    [Fact]
    public void Detect_SaysNothing_WhenItReadNothing()
    {
        // No inputs is not evidence of no need — it is evidence of nothing read. It must not
        // provision, and it must not claim the app is storage-free either.
        var need = Detector.Detect(new ObjectStorageScanInputs());

        Assert.False(need.Needed);
        Assert.Empty(need.Evidence);
    }
}
