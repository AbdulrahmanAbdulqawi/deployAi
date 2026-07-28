using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// The object-storage feature shipped complete except for one thing: nothing ever created the
/// storage deploy target that provisioning looks for. <c>DeploymentPartRoles.Storage</c> was
/// referenced in exactly one place in the whole codebase — the predicate testing for it — so
/// <c>ProvisionAsync</c> returned null for every project, and the provider, the env wiring and
/// twelve endpoints could not be reached at all.
/// </summary>
public class ObjectStorageProvisioningTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task ProvisionAsync_CreatesTheStorageLink_ForAProjectThatHasNoneYet()
    {
        // A normally-deployed app: server, website, database. No storage target, because nothing
        // in the product could produce one. This used to return null -- "This app doesn't use
        // object storage" -- for every app that existed.
        var h = Harness.Create();

        var result = await h.Service.ProvisionAsync(UserId, h.ProjectId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("yemenconnect", result!.Bucket);
        h.Storage.Verify(s => s.CreateBucketAsync(
            It.IsAny<ProviderCredentials>(), "yemenconnect", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProvisionAsync_WritesTheKeysTheAppReads_OntoTheServer()
    {
        var h = Harness.Create();

        var result = await h.Service.ProvisionAsync(UserId, h.ProjectId, CancellationToken.None);

        // The .NET configuration-binding shape, which is what an app reading Storage:* sees.
        Assert.Equal(
            ["Storage__Endpoint", "Storage__Region", "Storage__Bucket", "Storage__AccessKey", "Storage__SecretKey"],
            result!.AppliedKeys);

        // Onto the server, never the website: the website has no use for them and the access key
        // has no business in a frontend container.
        h.Management.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(), "api-uuid",
            It.IsAny<UpsertProviderEnvVarRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
        h.Management.Verify(m => m.UpsertEnvVarAsync(
            It.IsAny<ProviderCredentials>(), "web-uuid",
            It.IsAny<UpsertProviderEnvVarRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProvisionAsync_KeepsTheKeysSecret()
    {
        var h = Harness.Create();
        var written = new List<UpsertProviderEnvVarRequest>();
        h.Management.Setup(m => m.UpsertEnvVarAsync(
                It.IsAny<ProviderCredentials>(), It.IsAny<string>(),
                It.IsAny<UpsertProviderEnvVarRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ProviderCredentials _, string _, UpsertProviderEnvVarRequest r, CancellationToken _) => written.Add(r))
            .ReturnsAsync(new ProviderEnvVar("id", "k", "v", ProviderEnvVarTypes.Plain, [], false));

        await h.Service.ProvisionAsync(UserId, h.ProjectId, CancellationToken.None);

        Assert.Equal(ProviderEnvVarTypes.Secret, written.Single(r => r.Key == "Storage__AccessKey").Type);
        Assert.Equal(ProviderEnvVarTypes.Secret, written.Single(r => r.Key == "Storage__SecretKey").Type);
        Assert.Equal(ProviderEnvVarTypes.Plain, written.Single(r => r.Key == "Storage__Bucket").Type);
    }

    [Fact]
    public async Task ProvisionAsync_IsIdempotent_ReusingTheBucketTheAppAlreadyOwns()
    {
        // Re-running must not mint a second bucket. The link records the name for exactly this.
        var h = Harness.Create();

        await h.Service.ProvisionAsync(UserId, h.ProjectId, CancellationToken.None);
        await h.Service.ProvisionAsync(UserId, h.ProjectId, CancellationToken.None);

        var storageTargets = h.Db.DeployTargets
            .Where(t => t.ProjectId == h.ProjectId)
            .AsEnumerable()
            .Count(t => DeployTargetConfig.Parse(t.ConfigJson).IsStorageTarget);

        Assert.Equal(1, storageTargets);
        h.Storage.Verify(s => s.CreateBucketAsync(
            It.IsAny<ProviderCredentials>(), "yemenconnect", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProvisionAsync_RefusesWhenThereIsNoServerToWireInto()
    {
        // Creating a bucket nothing can reach is worse than declining: it costs money and looks done.
        var h = Harness.Create(includeServer: false);

        var ex = await Assert.ThrowsAsync<DeployAI.Core.Exceptions.DeployAIException>(
            () => h.Service.ProvisionAsync(UserId, h.ProjectId, CancellationToken.None));

        Assert.Equal("storage_no_server", ex.ErrorCode);
        h.Storage.Verify(s => s.CreateBucketAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Harness
    {
        public ObjectStorageProvisioningService Service { get; init; } = null!;
        public DeployAIDbContext Db { get; init; } = null!;
        public Guid ProjectId { get; init; }
        public Mock<IObjectStorageProvider> Storage { get; init; } = null!;
        public Mock<IProviderManagement> Management { get; init; } = null!;

        public static Harness Create(bool includeServer = true)
        {
            var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

            var projectId = Guid.NewGuid();
            var deployCredentialId = Guid.NewGuid();
            var storageCredentialId = Guid.NewGuid();

            db.ProviderCredentials.AddRange(
                new ProviderCredential
                {
                    Id = deployCredentialId,
                    UserId = UserId,
                    ProviderName = "coolify",
                    Kind = CredentialKind.Deployment,
                    TokenEncrypted = [1],
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new ProviderCredential
                {
                    Id = storageCredentialId,
                    UserId = UserId,
                    ProviderName = "hetzner-storage",
                    Kind = CredentialKind.ObjectStorage,
                    TokenEncrypted = [2],
                    CreatedAt = DateTimeOffset.UtcNow
                });

            var targets = new List<DeployTarget>
            {
                Target(projectId, deployCredentialId, "website", "web-uuid")
            };
            if (includeServer)
            {
                targets.Add(Target(projectId, deployCredentialId, "server", "api-uuid"));
            }

            db.Projects.Add(new Project
            {
                Id = projectId,
                UserId = UserId,
                Name = "yemenConnect",
                GitHubRepoFullName = "tester/yemenConnect",
                DefaultBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                DeployTargets = targets
            });
            db.SaveChanges();
            db.ChangeTracker.Clear();

            var encryption = new Mock<IEncryptionService>();
            // The storage connection's token is a JSON payload of endpoint + region + keys.
            encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns(
                StorageCredentialStorage.Serialize(
                    "https://fsn1.your-objectstorage.com", "fsn1", "AK123", "SK456"));

            var storage = new Mock<IObjectStorageProvider>();
            storage.SetupGet(s => s.ProviderName).Returns("hetzner-storage");
            storage.Setup(s => s.CreateBucketAsync(
                    It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProviderCredentials _, string name, CancellationToken _) =>
                    new StorageBucket(name, DateTimeOffset.UtcNow));

            var storageFactory = new Mock<IObjectStorageProviderFactory>();
            storageFactory.Setup(f => f.GetObjectStorage(It.IsAny<string>())).Returns(storage.Object);

            var management = new Mock<IProviderManagement>();
            management.Setup(m => m.UpsertEnvVarAsync(
                    It.IsAny<ProviderCredentials>(), It.IsAny<string>(),
                    It.IsAny<UpsertProviderEnvVarRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProviderEnvVar("id", "k", "v", ProviderEnvVarTypes.Plain, [], false));

            var managementFactory = new Mock<IProviderManagementFactory>();
            managementFactory.Setup(f => f.GetManagement(It.IsAny<string>())).Returns(management.Object);

            return new Harness
            {
                Db = db,
                ProjectId = projectId,
                Storage = storage,
                Management = management,
                Service = new ObjectStorageProvisioningService(
                    db, storageFactory.Object, managementFactory.Object, encryption.Object)
            };
        }

        private static DeployTarget Target(Guid projectId, Guid credentialId, string role, string providerProjectId) =>
            new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                ProviderName = "coolify",
                CredentialId = credentialId,
                ProviderProjectId = providerProjectId,
                ConfigJson = $$"""{"role":"{{role}}","framework":"docker"}""",
                CreatedAt = DateTimeOffset.UtcNow
            };
    }
}
