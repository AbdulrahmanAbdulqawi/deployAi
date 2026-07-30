using System.Net;
using System.Text;
using DeployAI.Core.Deployments;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>Implements repo/project split-origin readiness scanning by fetching the relevant files from GitHub and evaluating them via <see cref="SplitOriginReadinessEvaluator"/>.</summary>
public sealed class DeploymentReadinessService : IDeploymentReadinessService
{
    private readonly DeployAIDbContext _db;
    private readonly IGitHubService _gitHubService;
    private readonly IEncryptionService _encryption;
    private readonly HttpClient _httpClient;

    public DeploymentReadinessService(
        DeployAIDbContext db,
        IGitHubService gitHubService,
        IEncryptionService encryption,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _gitHubService = gitHubService;
        _encryption = encryption;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<DeploymentReadinessResult> ScanRepositoryAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        IReadOnlyList<DeploymentPlanPart> parts,
        CancellationToken cancellationToken)
    {
        var commitSha = await ResolveGitRefAsync(accessToken, owner, repo, gitRef, cancellationToken);
        var usesSplitOrigin = SplitOriginDetection.PlanUsesSplitOrigin(parts);
        var usesCompose = SplitOriginDetection.PlanUsesSingleOriginCompose(parts);

        // Most repos have at most one (website, server) pair, in which case this falls back to
        // the original single-pair resolution below. A repo can define two complete pairs at once
        // (e.g. Vercel+Railway alongside a Coolify+Coolify full stack); FindWebsitePart/FindServerPart
        // would silently scan only the first pair found and never flag missing files for the other.
        var pairs = SplitOriginDetection.ResolveProviderPairs(parts);
        if (pairs.Count > 1)
        {
            return await EvaluatePairsAsync(
                pairs,
                commitSha,
                accessToken,
                owner,
                repo,
                commitSha ?? gitRef,
                resolveProbeUrl: null,
                cancellationToken);
        }

        var website = SplitOriginDetection.FindWebsitePart(parts);
        var server = SplitOriginDetection.FindServerPart(parts);

        if ((!usesSplitOrigin && !usesCompose) || website is null || server is null)
        {
            return new DeploymentReadinessResult(
                IsReady: true,
                CommitSha: commitSha,
                UsesSplitOrigin: false,
                MissingFiles: [],
                Warnings: []);
        }

        var (missing, warnings) = await EvaluatePairAsync(
            accessToken,
            owner,
            repo,
            commitSha ?? gitRef,
            website,
            server,
            resolveProbeUrl: null,
            cancellationToken);

        return new DeploymentReadinessResult(
            IsReady: SplitOriginReadinessEvaluator.IsReady(missing),
            CommitSha: commitSha,
            UsesSplitOrigin: usesSplitOrigin,
            MissingFiles: missing,
            Warnings: warnings,
            WebsiteProviderName: website.ProviderName,
            ServerProviderName: server.ProviderName,
            UsesSingleOriginCompose: usesCompose);
    }

    public async Task<DeploymentReadinessResult> ScanProjectAsync(
        Guid projectId,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects
            .Include(p => p.DeployTargets)
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        if (project is null)
        {
            return new DeploymentReadinessResult(false, null, false, [], ["Project not found."]);
        }

        var repoParts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (repoParts.Length != 2)
        {
            return new DeploymentReadinessResult(false, null, false, [], ["Invalid GitHub repository name."]);
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var token = _encryption.Decrypt(user.GitHubTokenEncrypted);
        var branch = string.IsNullOrWhiteSpace(gitRef) ? project.DefaultBranch : gitRef;
        var parts = DeployTargetPlanParts.FromTargets(project.DeployTargets);

        var commitSha = await ResolveGitRefAsync(token, repoParts[0], repoParts[1], branch, cancellationToken);
        var usesSplitOrigin = SplitOriginDetection.PlanUsesSplitOrigin(parts);
        var usesCompose = SplitOriginDetection.PlanUsesSingleOriginCompose(parts);

        var pairs = SplitOriginDetection.ResolveProviderPairs(parts);
        if (pairs.Count > 1)
        {
            return await EvaluatePairsAsync(
                pairs,
                commitSha,
                token,
                repoParts[0],
                repoParts[1],
                commitSha ?? branch,
                resolveProbeUrl: ct => ResolveVercelWebsiteUrlAsync(project, ct),
                cancellationToken);
        }

        var website = SplitOriginDetection.FindWebsitePart(parts);
        var server = SplitOriginDetection.FindServerPart(parts);

        if ((!usesSplitOrigin && !usesCompose) || website is null || server is null)
        {
            return new DeploymentReadinessResult(true, commitSha, false, [], []);
        }

        var (missing, warnings) = await EvaluatePairAsync(
            token,
            repoParts[0],
            repoParts[1],
            commitSha ?? branch,
            website,
            server,
            resolveProbeUrl: ct => ResolveVercelWebsiteUrlAsync(project, ct),
            cancellationToken);

        return new DeploymentReadinessResult(
            IsReady: SplitOriginReadinessEvaluator.IsReady(missing),
            CommitSha: commitSha,
            UsesSplitOrigin: usesSplitOrigin,
            MissingFiles: missing,
            Warnings: warnings,
            WebsiteProviderName: website.ProviderName,
            ServerProviderName: server.ProviderName,
            UsesSingleOriginCompose: usesCompose);
    }

    private async Task<(IReadOnlyList<MissingDeploymentFile> MissingFiles, IReadOnlyList<string> Warnings)> EvaluatePairAsync(
        string token,
        string owner,
        string repo,
        string gitRef,
        DeploymentPlanPart website,
        DeploymentPlanPart server,
        Func<CancellationToken, Task<string?>>? resolveProbeUrl,
        CancellationToken cancellationToken)
    {
        // Compose-awareness lives here rather than at the call sites so it survives the multi-pair
        // path: a repo can declare a Vercel+Railway pair alongside a Coolify compose pair, and each
        // pair needs the readiness files for its own shape. Scanning for split-origin scaffolding
        // in a compose stack reports files missing that the shape never needed.
        var usesCompose = SplitOriginDetection.IsSingleOriginComposeStack(
            website.Framework, server.Framework, website.ProviderName, server.ProviderName);

        var paths = usesCompose
            ? SingleOriginComposeReadinessEvaluator.BuildAllScanPaths(website, server).ToList()
            : SplitOriginReadinessEvaluator.BuildAllScanPaths(website, server).ToList();
        var fileContents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            fileContents[path] = await _gitHubService.GetFileContentAsync(
                token,
                owner,
                repo,
                path,
                gitRef,
                cancellationToken);
        }

        var missing = SplitOriginDetection.EvaluateRepositoryFiles(!usesCompose, website, server, fileContents);
        var warnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(website.Framework) &&
            CrossProviderUrlWiring.UsesRelativeApiPaths(website.Framework) &&
            !SplitOriginDetection.IsCoolifyFullStack(website.ProviderName, server.ProviderName))
        {
            var probeUrl = resolveProbeUrl is null ? null : await resolveProbeUrl(cancellationToken);
            var proxy405 = await ProbeVercelApiPostReturns405Async(probeUrl, cancellationToken);
            if (proxy405 == true)
            {
                warnings.Add("POST to vercel.app/api returns 405 — split-origin setup with direct Railway API URL is required.");
            }
        }

        return (missing, warnings);
    }

    private async Task<DeploymentReadinessResult> EvaluatePairsAsync(
        IReadOnlyList<(DeploymentPlanPart Website, DeploymentPlanPart Server)> pairs,
        string? commitSha,
        string token,
        string owner,
        string repo,
        string gitRef,
        Func<CancellationToken, Task<string?>>? resolveProbeUrl,
        CancellationToken cancellationToken)
    {
        var allMissing = new List<MissingDeploymentFile>();
        var allWarnings = new List<string>();
        var allReady = true;

        foreach (var pair in pairs)
        {
            var pairLabel = $"{pair.Website.ProviderName}+{pair.Server.ProviderName}";
            var (missing, warnings) = await EvaluatePairAsync(
                token,
                owner,
                repo,
                gitRef,
                pair.Website,
                pair.Server,
                resolveProbeUrl,
                cancellationToken);

            allReady &= SplitOriginReadinessEvaluator.IsReady(missing);
            allMissing.AddRange(missing.Select(file => file with { Path = $"[{pairLabel}] {file.Path}" }));
            allWarnings.AddRange(warnings.Select(warning => $"[{pairLabel}] {warning}"));
        }

        return new DeploymentReadinessResult(
            IsReady: allReady,
            CommitSha: commitSha,
            UsesSplitOrigin: true,
            MissingFiles: allMissing,
            Warnings: allWarnings,
            WebsiteProviderName: null,
            ServerProviderName: null);
    }

    private async Task<string?> ResolveGitRefAsync(
        string accessToken,
        string owner,
        string repo,
        string gitRef,
        CancellationToken cancellationToken)
    {
        if (gitRef.Length >= 7 && gitRef.All(c => Uri.IsHexDigit(c)))
        {
            return gitRef;
        }

        return await _gitHubService.GetBranchHeadShaAsync(accessToken, owner, repo, gitRef, cancellationToken);
    }

    private async Task<string?> ResolveVercelWebsiteUrlAsync(Project project, CancellationToken cancellationToken)
    {
        var sync = ProjectEnvironmentSyncState.Parse(project.EnvironmentSyncJson);
        if (!string.IsNullOrWhiteSpace(sync?.ResolvedWebsiteUrl))
        {
            return sync.ResolvedWebsiteUrl;
        }

        var vercelTarget = project.DeployTargets
            .FirstOrDefault(target => string.Equals(target.ProviderName, "vercel", StringComparison.OrdinalIgnoreCase));
        if (vercelTarget is null)
        {
            return null;
        }

        return await _db.DeploymentTargets
            .Where(target =>
                target.DeployTargetId == vercelTarget.Id &&
                target.DeployUrl != null &&
                target.Status == DeploymentStatuses.Success)
            .OrderByDescending(target => target.CompletedAt)
            .Select(target => target.DeployUrl)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool?> ProbeVercelApiPostReturns405Async(string? vercelUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vercelUrl))
        {
            return null;
        }

        var baseUrl = vercelUrl.Trim().TrimEnd('/');
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/health");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.StatusCode == HttpStatusCode.MethodNotAllowed;
        }
        catch
        {
            return null;
        }
    }
}
