using DeployAI.Core.Exceptions;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

/// <summary>Creates/removes the actual GitHub push webhook on a repo when a project's auto-deploy setting is toggled.</summary>
public interface IGitHubWebhookRegistrationService
{
    /// <summary>Registers or deregisters the repo's GitHub webhook to match the desired auto-deploy state.</summary>
    Task SetAutoDeployAsync(Project project, bool enabled, CancellationToken cancellationToken);
}

public sealed class GitHubWebhookRegistrationService : IGitHubWebhookRegistrationService
{
    private readonly DeployAIDbContext _db;
    private readonly IGitHubService _gitHubService;
    private readonly IEncryptionService _encryption;
    private readonly AppOptions _appOptions;
    private readonly GitHubOptions _gitHubOptions;

    public GitHubWebhookRegistrationService(
        DeployAIDbContext db,
        IGitHubService gitHubService,
        IEncryptionService encryption,
        IOptions<AppOptions> appOptions,
        IOptions<GitHubOptions> gitHubOptions)
    {
        _db = db;
        _gitHubService = gitHubService;
        _encryption = encryption;
        _appOptions = appOptions.Value;
        _gitHubOptions = gitHubOptions.Value;
    }

    public async Task SetAutoDeployAsync(Project project, bool enabled, CancellationToken cancellationToken)
    {
        if (enabled == project.AutoDeployEnabled)
        {
            return;
        }

        var repoParts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (repoParts.Length != 2)
        {
            throw new DeployAIException("invalid_repository", "Invalid GitHub repository name.");
        }

        if (string.IsNullOrWhiteSpace(_gitHubOptions.WebhookSecret))
        {
            throw new DeployAIException(
                "webhook_not_configured",
                "Auto-deploy is not available until GitHub:WebhookSecret is configured on the API.");
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var token = _encryption.Decrypt(user.GitHubTokenEncrypted);

        if (enabled)
        {
            if (project.GitHubWebhookId is null)
            {
                var webhookUrl = $"{_appOptions.ApiUrl.TrimEnd('/')}/api/webhooks/github";
                var hookId = await _gitHubService.CreateRepoWebhookAsync(
                    token,
                    repoParts[0],
                    repoParts[1],
                    webhookUrl,
                    _gitHubOptions.WebhookSecret,
                    cancellationToken);
                project.GitHubWebhookId = hookId;
            }

            project.AutoDeployEnabled = true;
            return;
        }

        if (project.GitHubWebhookId is long hookIdToDelete)
        {
            await _gitHubService.DeleteRepoWebhookAsync(
                token,
                repoParts[0],
                repoParts[1],
                hookIdToDelete,
                cancellationToken);
            project.GitHubWebhookId = null;
        }

        project.AutoDeployEnabled = false;
    }
}
