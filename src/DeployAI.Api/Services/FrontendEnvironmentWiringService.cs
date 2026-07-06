using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

public interface IFrontendEnvironmentWiringService
{
    Task WireApiUrlForWebsiteTargetAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken);
}

public sealed class FrontendEnvironmentWiringService : IFrontendEnvironmentWiringService
{
    private readonly DeployAIDbContext _db;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IProviderCredentialTokenService _tokens;

    public FrontendEnvironmentWiringService(
        DeployAIDbContext db,
        IProviderManagementFactory managementFactory,
        IProviderCredentialTokenService tokens)
    {
        _db = db;
        _managementFactory = managementFactory;
        _tokens = tokens;
    }

    public async Task WireApiUrlForWebsiteTargetAsync(
        Guid deploymentId,
        DeploymentTarget websiteTarget,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(websiteTarget.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var deployment = await _db.Deployments
            .Include(d => d.Targets)
            .Include(d => d.Project)
            .ThenInclude(p => p.DeployTargets)
            .ThenInclude(t => t.Credential)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, cancellationToken);

        if (deployment is null)
        {
            return;
        }

        var apiUrl = deployment.Targets
            .FirstOrDefault(t =>
                string.Equals(t.ProviderName, "railway", StringComparison.OrdinalIgnoreCase) &&
                t.Status == DeploymentStatuses.Success &&
                !string.IsNullOrWhiteSpace(t.DeployUrl))
            ?.DeployUrl;

        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return;
        }

        var websiteDeployTarget = deployment.Project.DeployTargets
            .FirstOrDefault(t => t.Id == websiteTarget.DeployTargetId);
        if (websiteDeployTarget?.Credential is null)
        {
            return;
        }

        var websiteConfig = DeployTargetConfig.Parse(websiteDeployTarget.ConfigJson);
        var envKeys = ResolveApiEnvKeys(websiteConfig.Framework).ToList();
        if (envKeys.Count == 0)
        {
            envKeys.Add("API_URL");
        }

        var management = _managementFactory.GetManagement("vercel");
        var token = await _tokens.GetTokenAsync(websiteDeployTarget.Credential, cancellationToken);
        var credentials = new ProviderCredentials(token);

        foreach (var key in envKeys)
        {
            await management.UpsertEnvVarAsync(
                credentials,
                websiteDeployTarget.ProviderProjectId,
                new UpsertProviderEnvVarRequest(
                    key,
                    apiUrl,
                    "encrypted",
                    ["production", "preview", "development"]),
                cancellationToken);
        }
    }

    internal static IReadOnlyList<string> ResolveApiEnvKeys(string? framework)
    {
        return framework?.ToLowerInvariant() switch
        {
            "next" or "nextjs" or "next.js" => ["NEXT_PUBLIC_API_URL", "API_URL"],
            "nuxt" => ["NUXT_PUBLIC_API_URL", "API_URL"],
            "vite" or "react" => ["VITE_API_URL", "API_URL"],
            "angular" => ["NG_APP_API_URL", "API_URL"],
            "sveltekit" or "astro" => ["PUBLIC_API_URL", "API_URL"],
            _ => ["API_URL"]
        };
    }
}
