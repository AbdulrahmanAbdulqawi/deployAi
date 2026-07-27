using DeployAI.Core.Deployments;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Email;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

/// <summary>Emails the project owner when a deployment finishes, honoring their notification preferences (success/failure).</summary>
public interface IDeploymentNotificationService
{
    /// <summary>Sends a completion email for a deployment if the owner's preferences call for one at this outcome.</summary>
    Task NotifyDeploymentCompletedAsync(Guid deploymentId, CancellationToken cancellationToken);
}

public sealed class DeploymentNotificationService : IDeploymentNotificationService
{
    private readonly DeployAIDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly AppOptions _appOptions;

    public DeploymentNotificationService(
        DeployAIDbContext db,
        IEmailSender emailSender,
        IOptions<AppOptions> appOptions)
    {
        _db = db;
        _emailSender = emailSender;
        _appOptions = appOptions.Value;
    }

    public async Task NotifyDeploymentCompletedAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        var deployment = await _db.Deployments
            .AsNoTracking()
            .Include(d => d.Project)
            .ThenInclude(p => p.User)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, cancellationToken);

        if (deployment?.Project.User is null)
        {
            return;
        }

        var prefs = await _db.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == deployment.Project.UserId, cancellationToken);

        var emailOnSuccess = prefs?.EmailOnSuccess ?? true;
        var emailOnFailure = prefs?.EmailOnFailure ?? true;
        var isSuccess = deployment.Status == DeploymentStatuses.Success;

        if (isSuccess && !emailOnSuccess)
        {
            return;
        }

        if (!isSuccess && !emailOnFailure)
        {
            return;
        }

        var email = deployment.Project.User.Email;
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var statusLabel = deployment.Status switch
        {
            DeploymentStatuses.Success => "succeeded",
            DeploymentStatuses.Failed => "failed",
            DeploymentStatuses.Partial => "partially succeeded",
            DeploymentStatuses.Cancelled => "was cancelled",
            _ => deployment.Status
        };

        var subject = $"DeployAI: {deployment.Project.Name} publish {statusLabel}";
        var body =
            $"""
            Your publish for "{deployment.Project.Name}" {statusLabel}.

            Branch: {deployment.Branch}
            Status: {deployment.Status}

            View details: {_appOptions.FrontendUrl.TrimEnd('/')}/projects/{deployment.ProjectId}/deploy/{deployment.Id}
            """;

        await _emailSender.SendAsync(email, subject, body, cancellationToken);
    }
}
