using DeployAI.Api.Controllers;
using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class StorageControllerTests
{
    private const string Endpoint = "https://hel1.your-objectstorage.com";

    [Fact]
    public async Task CreateConnection_PersistsWithObjectStorageKind()
    {
        var (db, userId) = await CreateDbAsync();
        var encryption = CreateEncryption();
        var controller = CreateController(db, userId, encryption.Object, validates: true);

        var result = await controller.CreateConnection(Request(), CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        var stored = await db.ProviderCredentials.SingleAsync();
        Assert.Equal(CredentialKind.ObjectStorage, stored.Kind);
        Assert.Equal(ObjectStorageProviderNames.Hetzner, stored.ProviderName);
        Assert.True(stored.IsValid);

        // The endpoint and keys are packed into the encrypted envelope, never stored plainly.
        encryption.Verify(
            e => e.Encrypt(It.Is<string>(json => json.Contains(Endpoint) && json.Contains("secret-key"))),
            Times.Once);
    }

    [Fact]
    public async Task CreateConnection_RejectsInvalidCredentials_WithoutPersisting()
    {
        var (db, userId) = await CreateDbAsync();
        var controller = CreateController(db, userId, CreateEncryption().Object, validates: false);

        var ex = await Assert.ThrowsAsync<DeployAIException>(() =>
            controller.CreateConnection(Request(), CancellationToken.None));

        Assert.Equal("storage_credentials_invalid", ex.ErrorCode);
        Assert.Empty(await db.ProviderCredentials.ToListAsync());
    }

    [Fact]
    public async Task CreateConnection_RejectsMalformedEndpoint()
    {
        var (db, userId) = await CreateDbAsync();
        var controller = CreateController(db, userId, CreateEncryption().Object, validates: true);

        var ex = await Assert.ThrowsAsync<DeployAIException>(() =>
            controller.CreateConnection(Request(endpoint: "not-a-url"), CancellationToken.None));

        Assert.Equal("storage_connection_invalid", ex.ErrorCode);
        Assert.Empty(await db.ProviderCredentials.ToListAsync());
    }

    /// <summary>
    /// The reason CredentialKind exists: GET /api/credentials feeds deploy-target pickers,
    /// so a storage connection must never appear there.
    /// </summary>
    [Fact]
    public async Task StorageConnections_AreExcludedFrom_DeploymentCredentialsList()
    {
        var (db, userId) = await CreateDbAsync();
        db.ProviderCredentials.Add(new ProviderCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProviderName = ProviderNameValues.Coolify,
            Kind = CredentialKind.Deployment,
            Label = "Homelab",
            TokenEncrypted = [1],
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.ProviderCredentials.Add(new ProviderCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProviderName = ObjectStorageProviderNames.Hetzner,
            Kind = CredentialKind.ObjectStorage,
            Label = "Media",
            TokenEncrypted = [2],
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("{}");

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserId).Returns(userId);

        var credentialsController = new CredentialsController(
            db,
            currentUser.Object,
            new Mock<IProviderFactory>().Object,
            encryption.Object,
            new Mock<IProviderCredentialTokenService>().Object);

        var listed = await credentialsController.List(CancellationToken.None);
        var payload = Assert.IsType<OkObjectResult>(listed).Value!;
        var credentials = (System.Collections.IEnumerable)payload.GetType()
            .GetProperty("credentials")!.GetValue(payload)!;

        var names = credentials.Cast<object>()
            .Select(c => c.GetType().GetProperty("providerName")!.GetValue(c) as string)
            .ToList();

        Assert.Contains(ProviderNameValues.Coolify, names);
        Assert.DoesNotContain(ObjectStorageProviderNames.Hetzner, names);
    }

    [Fact]
    public async Task DeleteObjects_RejectsBatchesOverTheS3Limit()
    {
        var (db, userId) = await CreateDbAsync();
        var connectionId = await SeedConnectionAsync(db, userId);
        var controller = CreateController(db, userId, CreateEncryption().Object, validates: true);

        var tooMany = Enumerable.Range(0, ObjectStorageLimits.MaxDeleteBatchSize + 1)
            .Select(i => $"key-{i}")
            .ToList();

        var ex = await Assert.ThrowsAsync<DeployAIException>(() =>
            controller.DeleteObjects(
                connectionId, "bucket",
                new StorageController.DeleteObjectsRequest(tooMany),
                CancellationToken.None));

        Assert.Equal("storage_delete_batch_too_large", ex.ErrorCode);
    }

    [Fact]
    public async Task DeleteObjects_RejectsEmptySelection()
    {
        var (db, userId) = await CreateDbAsync();
        var connectionId = await SeedConnectionAsync(db, userId);
        var controller = CreateController(db, userId, CreateEncryption().Object, validates: true);

        var ex = await Assert.ThrowsAsync<DeployAIException>(() =>
            controller.DeleteObjects(
                connectionId, "bucket",
                new StorageController.DeleteObjectsRequest([]),
                CancellationToken.None));

        Assert.Equal("storage_no_keys", ex.ErrorCode);
    }

    [Fact]
    public async Task ListBuckets_DoesNotExposeAnotherUsersConnection()
    {
        var (db, userId) = await CreateDbAsync();
        var otherUsersConnection = await SeedConnectionAsync(db, Guid.NewGuid());
        var controller = CreateController(db, userId, CreateEncryption().Object, validates: true);

        var ex = await Assert.ThrowsAsync<DeployAIException>(() =>
            controller.ListBuckets(otherUsersConnection, CancellationToken.None));

        Assert.Equal("not_found", ex.ErrorCode);
    }

    private static StorageController.CreateStorageConnectionRequest Request(string endpoint = Endpoint) =>
        new(null, endpoint, "hel1", "access-key", "secret-key", "Media");

    private static Mock<IEncryptionService> CreateEncryption()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Encrypt(It.IsAny<string>())).Returns([9, 9, 9]);
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>()))
            .Returns(StorageCredentialStorage.Serialize(Endpoint, "hel1", "access-key", "secret-key"));
        return encryption;
    }

    private static async Task<(DeployAIDbContext Db, Guid UserId)> CreateDbAsync()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (db, userId);
    }

    private static async Task<Guid> SeedConnectionAsync(DeployAIDbContext db, Guid userId)
    {
        var id = Guid.NewGuid();
        db.ProviderCredentials.Add(new ProviderCredential
        {
            Id = id,
            UserId = userId,
            ProviderName = ObjectStorageProviderNames.Hetzner,
            Kind = CredentialKind.ObjectStorage,
            Label = $"Media-{id:N}",
            TokenEncrypted = [1],
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static StorageController CreateController(
        DeployAIDbContext db,
        Guid userId,
        IEncryptionService encryption,
        bool validates)
    {
        var provider = new Mock<IObjectStorageProvider>();
        provider.Setup(p => p.ProviderName).Returns(ObjectStorageProviderNames.Hetzner);
        provider.Setup(p => p.DisplayName).Returns("Hetzner Object Storage");
        provider.Setup(p => p.ValidateCredentialsAsync(It.IsAny<ProviderCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(validates);

        var factory = new Mock<IObjectStorageProviderFactory>();
        factory.Setup(f => f.GetObjectStorage(ObjectStorageProviderNames.Hetzner)).Returns(provider.Object);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserId).Returns(userId);

        return new StorageController(db, currentUser.Object, factory.Object, encryption);
    }
}
