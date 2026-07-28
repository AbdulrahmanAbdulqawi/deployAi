using System.Text.Json;
using System.Text.Json.Nodes;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>Implements switching a project's default branch and optionally triggering a deployment from it, validating the branch actually exists on GitHub first.</summary>
public sealed class ProjectBranchDeployService : IProjectBranchDeployService
{
    private readonly DeployAIDbContext _db;
    private readonly IGitHubService _gitHubService;
    private readonly IDeploymentOrchestrator _deploymentOrchestrator;
    private readonly IEncryptionService _encryption;

    public ProjectBranchDeployService(
        DeployAIDbContext db,
        IGitHubService gitHubService,
        IDeploymentOrchestrator deploymentOrchestrator,
        IEncryptionService encryption)
    {
        _db = db;
        _gitHubService = gitHubService;
        _deploymentOrchestrator = deploymentOrchestrator;
        _encryption = encryption;
    }

    public async Task<UseBranchDeployResult> UseBranchAndDeployAsync(
        Guid userId,
        Guid projectId,
        string branch,
        bool triggerFullDeploy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            throw new DeployAIException("invalid_request", "A branch name is required.");
        }

        var project = await _db.Projects.FirstOrDefaultAsync(
            p => p.Id == projectId && p.UserId == userId,
            cancellationToken);
        if (project is null)
        {
            throw new DeployAIException("project_not_found", "We couldn't find that app.");
        }

        var repoParts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (repoParts.Length != 2)
        {
            throw new DeployAIException("invalid_repository", "Invalid GitHub repository name.");
        }

        var token = await GetGitHubTokenAsync(userId, cancellationToken);
        var branchSha = await _gitHubService.GetBranchHeadShaAsync(
            token,
            repoParts[0],
            repoParts[1],
            branch.Trim(),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(branchSha))
        {
            throw new DeployAIException("branch_not_found", $"We couldn't find branch `{branch}` on GitHub.");
        }

        project.DefaultBranch = branch.Trim();
        project.UpdatedAt = DateTimeOffset.UtcNow;
        var state = ParseSetupState(project.DeploymentSetupJson);
        state["setupBranch"] = branch.Trim();
        state["setupCompletedAt"] = DateTimeOffset.UtcNow.ToString("O");
        project.DeploymentSetupJson = state.ToJsonString();
        await _db.SaveChangesAsync(cancellationToken);

        if (!triggerFullDeploy)
        {
            return new UseBranchDeployResult(
                branch.Trim(),
                null,
                $"Project will deploy from `{branch.Trim()}` on the next publish.");
        }

        var deployment = await _deploymentOrchestrator.TriggerAsync(
            projectId,
            userId,
            branch.Trim(),
            cancellationToken);

        return new UseBranchDeployResult(
            branch.Trim(),
            deployment.DeploymentId,
            $"Deploying from `{branch.Trim()}`.");
    }

    private async Task<string> GetGitHubTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken);
        return _encryption.Decrypt(user.GitHubTokenEncrypted);
    }

    private static JsonObject ParseSetupState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
