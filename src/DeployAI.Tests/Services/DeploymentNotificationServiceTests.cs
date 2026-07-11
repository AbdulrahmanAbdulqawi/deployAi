using DeployAI.Api.Services;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Email;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace DeployAI.Tests.Services;

public class DeploymentNotificationServiceTests
{
    [Fact]
    public async Task NotifyDeploymentCompletedAsync_WhenSuccessDisabled_DoesNotSendEmail()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();

        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            Email = "tester@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.NotificationPreferences.Add(new NotificationPreference
        {
            UserId = userId,
            EmailOnSuccess = false,
            EmailOnFailure = true,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "Demo",
            GitHubRepoFullName = "tester/demo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            ProjectId = projectId,
            Branch = "main",
            TriggeredBy = "user",
            Status = DeploymentStatuses.Success,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var emailSender = new Mock<IEmailSender>();
        var service = CreateService(db, emailSender.Object);
        await service.NotifyDeploymentCompletedAsync(deploymentId, CancellationToken.None);

        emailSender.Verify(
            sender => sender.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyDeploymentCompletedAsync_WhenFailureEnabled_SendsEmail()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();

        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            Email = "tester@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.Projects.Add(new Project
        {
            Id = projectId,
            UserId = userId,
            Name = "Demo",
            GitHubRepoFullName = "tester/demo",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            ProjectId = projectId,
            Branch = "main",
            TriggeredBy = "user",
            Status = DeploymentStatuses.Failed,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var emailSender = new Mock<IEmailSender>();
        var service = CreateService(db, emailSender.Object);
        await service.NotifyDeploymentCompletedAsync(deploymentId, CancellationToken.None);

        emailSender.Verify(
            sender => sender.SendAsync(
                "tester@example.com",
                It.Is<string>(subject => subject.Contains("failed")),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static DeployAIDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeployAIDbContext(options);
    }

    private static DeploymentNotificationService CreateService(DeployAIDbContext db, IEmailSender emailSender)
    {
        var appOptions = Options.Create(new AppOptions { FrontendUrl = "http://localhost:4200" });
        return new DeploymentNotificationService(db, emailSender, appOptions);
    }
}
