using DeployAI.Api.Controllers;
using DeployAI.Api.Services;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace DeployAI.Tests.Services;

public class CredentialsControllerCoolifyTests
{
    [Fact]
    public async Task Create_UpsertsCoolifyCredential_WhenLabelMatches()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        var existingId = Guid.NewGuid();
        var originalToken = CoolifyCredentialStorage.Serialize("https://old.example.com", "old-token");

        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.ProviderCredentials.Add(new ProviderCredential
        {
            Id = existingId,
            UserId = userId,
            ProviderName = ProviderNameValues.Coolify,
            Label = "Homelab",
            TokenEncrypted = [1, 2, 3],
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns(originalToken);
        encryption.Setup(e => e.Encrypt(It.IsAny<string>())).Returns([9, 9, 9]);

        var provider = new Mock<IDeploymentProvider>();
        provider.Setup(p => p.ProviderName).Returns(ProviderNameValues.Coolify);
        provider.Setup(p => p.DisplayName).Returns("Coolify");
        provider.Setup(p => p.ValidateCredentialsAsync(It.IsAny<ProviderCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.GetProvider(ProviderNameValues.Coolify)).Returns(provider.Object);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserId).Returns(userId);

        var tokens = new Mock<IProviderCredentialTokenService>();
        var controller = new CredentialsController(db, currentUser.Object, factory.Object, encryption.Object, tokens.Object);

        var result = await controller.Create(
            new CredentialsController.CreateCredentialRequest(
                ProviderNameValues.Coolify,
                "new-token",
                "Homelab",
                "https://coolify.example.com"),
            CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        Assert.Equal(1, await db.ProviderCredentials.CountAsync());
        var stored = await db.ProviderCredentials.SingleAsync();
        Assert.Equal(existingId, stored.Id);
        Assert.Equal([9, 9, 9], stored.TokenEncrypted);
        encryption.Verify(e => e.Encrypt(It.Is<string>(json => json.Contains("coolify.example.com") && json.Contains("new-token"))), Times.Once);
    }

    [Fact]
    public async Task List_ReturnsCoolifyInstanceUrl()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new DeployAIDbContext(options);
        var userId = Guid.NewGuid();
        var tokenJson = CoolifyCredentialStorage.Serialize("https://coolify.example.com", "token");

        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.ProviderCredentials.Add(new ProviderCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProviderName = ProviderNameValues.Coolify,
            Label = "Homelab",
            TokenEncrypted = [1],
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns(tokenJson);

        var factory = new Mock<IProviderFactory>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserId).Returns(userId);
        var tokens = new Mock<IProviderCredentialTokenService>();

        var controller = new CredentialsController(db, currentUser.Object, factory.Object, encryption.Object, tokens.Object);
        var result = await controller.List(CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var payload = ok.Value!;
        var credentialsProperty = payload.GetType().GetProperty("credentials");
        Assert.NotNull(credentialsProperty);
        var credentials = Assert.IsAssignableFrom<IEnumerable<object>>(credentialsProperty.GetValue(payload));
        var first = credentials.First();
        var instanceUrl = first.GetType().GetProperty("instanceUrl")?.GetValue(first) as string;
        Assert.Equal("https://coolify.example.com", instanceUrl);
    }
}
