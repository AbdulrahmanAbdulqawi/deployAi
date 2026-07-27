using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

/// <summary>Validates and processes GitHub push webhook deliveries, triggering auto-deploy for any project tracking the pushed branch.</summary>
public interface IGitHubWebhookHandler
{
    /// <summary>Verifies the payload's HMAC signature against the configured webhook secret, then triggers a deployment if a project tracks the pushed branch.</summary>
    Task HandlePushAsync(string payload, string? signatureHeader, CancellationToken cancellationToken);
}

public sealed class GitHubWebhookHandler : IGitHubWebhookHandler
{
    private readonly DeployAIDbContext _db;
    private readonly IDeploymentOrchestrator _deploymentOrchestrator;
    private readonly GitHubOptions _gitHubOptions;
    private readonly ILogger<GitHubWebhookHandler> _logger;

    public GitHubWebhookHandler(
        DeployAIDbContext db,
        IDeploymentOrchestrator deploymentOrchestrator,
        IOptions<GitHubOptions> gitHubOptions,
        ILogger<GitHubWebhookHandler> logger)
    {
        _db = db;
        _deploymentOrchestrator = deploymentOrchestrator;
        _gitHubOptions = gitHubOptions.Value;
        _logger = logger;
    }

    public async Task HandlePushAsync(string payload, string? signatureHeader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_gitHubOptions.WebhookSecret))
        {
            throw new DeployAIException("webhook_not_configured", "GitHub webhooks are not configured.");
        }

        if (!VerifySignature(payload, signatureHeader, _gitHubOptions.WebhookSecret))
        {
            throw new DeployAIException("invalid_signature", "Webhook signature verification failed.");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("ref", out var refElement) ||
            !root.TryGetProperty("repository", out var repositoryElement) ||
            !repositoryElement.TryGetProperty("full_name", out var fullNameElement))
        {
            return;
        }

        var gitRef = refElement.GetString();
        var repoFullName = fullNameElement.GetString();
        if (string.IsNullOrWhiteSpace(gitRef) || string.IsNullOrWhiteSpace(repoFullName))
        {
            return;
        }

        var projects = await _db.Projects
            .AsNoTracking()
            .Where(p =>
                p.AutoDeployEnabled &&
                p.GitHubRepoFullName == repoFullName)
            .ToListAsync(cancellationToken);

        foreach (var project in projects)
        {
            var expectedRef = $"refs/heads/{project.DefaultBranch}";
            if (!string.Equals(gitRef, expectedRef, StringComparison.Ordinal))
            {
                continue;
            }

            var hasActiveDeployment = await _db.Deployments.AnyAsync(
                d => d.ProjectId == project.Id &&
                     (d.Status == DeploymentStatuses.Pending || d.Status == DeploymentStatuses.InProgress),
                cancellationToken);
            if (hasActiveDeployment)
            {
                _logger.LogInformation(
                    "Skipping auto-deploy for project {ProjectId} because a deployment is already active.",
                    project.Id);
                continue;
            }

            try
            {
                await _deploymentOrchestrator.TriggerAsync(
                    project.Id,
                    project.UserId,
                    project.DefaultBranch,
                    cancellationToken,
                    DeploymentTriggeredBy.Webhook);
            }
            catch (DeployAIException ex) when (ex.ErrorCode is "deployment_not_ready" or "no_targets")
            {
                _logger.LogWarning(
                    "Auto-deploy skipped for project {ProjectId}: {Message}",
                    project.Id,
                    ex.Message);
            }
        }
    }

    private static bool VerifySignature(string payload, string? signatureHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || !signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = signatureHeader["sha256=".Length..];
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expected = Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }
}
