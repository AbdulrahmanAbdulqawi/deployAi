using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Infrastructure.GitHub;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Controllers;

/// <summary>
/// Browses the current user's GitHub repos on their behalf (using their stored GitHub OAuth
/// token) and inspects a repo's contents to classify how it should be deployed - build commands,
/// frameworks, database requirements, and the overall multi-part deployment plan.
/// </summary>
[ApiController]
[Authorize]
[Route("api/github")]
public sealed class GitHubController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IGitHubService _gitHubService;
    private readonly IFrontendBuildDetector _buildDetector;
    private readonly IDatabaseRequirementDetector _databaseRequirementDetector;
    private readonly IServerBuildProfileDiscovery _serverBuildProfileDiscovery;
    private readonly IRepositoryClassifier _repositoryClassifier;
    private readonly IEncryptionService _encryption;

    public GitHubController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IGitHubService gitHubService,
        IFrontendBuildDetector buildDetector,
        IDatabaseRequirementDetector databaseRequirementDetector,
        IServerBuildProfileDiscovery serverBuildProfileDiscovery,
        IRepositoryClassifier repositoryClassifier,
        IEncryptionService encryption)
    {
        _db = db;
        _currentUser = currentUser;
        _gitHubService = gitHubService;
        _buildDetector = buildDetector;
        _databaseRequirementDetector = databaseRequirementDetector;
        _serverBuildProfileDiscovery = serverBuildProfileDiscovery;
        _repositoryClassifier = repositoryClassifier;
        _encryption = encryption;
    }

    /// <summary>Lists the current user's GitHub repos, optionally filtered by name.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="perPage">Page size.</param>
    /// <param name="search">Optional substring filter on repo name.</param>
    [HttpGet("repos")]
    public async Task<IActionResult> ListRepos([FromQuery] int page = 1, [FromQuery] int perPage = 30, [FromQuery] string? search = null, CancellationToken cancellationToken = default)
    {
        var token = await GetGitHubTokenAsync(cancellationToken);
        var repos = await _gitHubService.ListReposAsync(token, page, perPage, search, cancellationToken);
        return Ok(new
        {
            repos = repos.Select(r => new
            {
                fullName = r.FullName,
                defaultBranch = r.DefaultBranch,
                @private = r.Private
            }),
            page,
            hasMore = repos.Count == perPage
        });
    }

    /// <summary>Lists the branches of a repo.</summary>
    /// <param name="owner">Repo owner/org.</param>
    /// <param name="repo">Repo name.</param>
    [HttpGet("repos/{owner}/{repo}/branches")]
    public async Task<IActionResult> ListBranches(string owner, string repo, CancellationToken cancellationToken)
    {
        var token = await GetGitHubTokenAsync(cancellationToken);
        var branches = await _gitHubService.ListBranchesAsync(token, owner, repo, cancellationToken);
        return Ok(new { branches = branches.Select(b => new { name = b.Name }) });
    }

    /// <summary>Lists the directories at a given path/ref in a repo, for the "pick a root folder" UI.</summary>
    /// <param name="owner">Repo owner/org.</param>
    /// <param name="repo">Repo name.</param>
    /// <param name="path">Path to list; empty/null lists the repo root.</param>
    /// <param name="ref">Branch, tag, or commit SHA; null uses the repo's default branch.</param>
    [HttpGet("repos/{owner}/{repo}/contents")]
    public async Task<IActionResult> ListContents(
        string owner,
        string repo,
        [FromQuery] string? path,
        [FromQuery] string? @ref,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = await GetGitHubTokenAsync(cancellationToken);
            var directories = await _gitHubService.ListContentsAsync(token, owner, repo, path, @ref, cancellationToken);
            return Ok(new
            {
                path = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Trim('/'),
                directories = directories.Select(d => new { name = d.Name, path = d.Path })
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("GitHub is busy", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeployAIException("github_rate_limit", ex.Message);
        }
        catch (HttpRequestException)
        {
            throw new DeployAIException("github_unavailable", "We couldn't read folders from GitHub right now. Try again in a moment.");
        }
    }

    /// <summary>
    /// Classifies a repo into a full deployment plan (one part per website/server/database piece,
    /// each with a suggested provider, framework, and build config). Takes into account whether the
    /// user has a Coolify connection, since that changes which plan shapes are viable. May return a
    /// clarifying question instead of a confident plan when the repo's structure is ambiguous.
    /// </summary>
    /// <param name="owner">Repo owner/org.</param>
    /// <param name="repo">Repo name.</param>
    /// <param name="ref">Branch, tag, or commit SHA; null uses the repo's default branch.</param>
    [HttpGet("repos/{owner}/{repo}/deployment-plan")]
    public async Task<IActionResult> GetDeploymentPlan(
        string owner,
        string repo,
        [FromQuery] string? @ref,
        CancellationToken cancellationToken)
    {
        var token = await GetGitHubTokenAsync(cancellationToken);
        var userId = _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
        var hasCoolify = await _db.ProviderCredentials.AnyAsync(
            c => c.UserId == userId && c.ProviderName == ProviderNameValues.Coolify,
            cancellationToken);
        var plan = await _repositoryClassifier.ClassifyAsync(
            token,
            owner,
            repo,
            @ref,
            new RepositoryClassificationOptions(hasCoolify),
            cancellationToken);

        return Ok(new
        {
            planKind = DeploymentPlanKindValues.ToApiValue(plan.PlanKind),
            parts = plan.Parts.Select(part => new
            {
                role = part.Role,
                providerName = part.ProviderName,
                rootDirectory = part.RootDirectory,
                serviceDirectory = part.ServiceDirectory,
                buildCommand = part.BuildCommand,
                installCommand = part.InstallCommand,
                startCommand = part.StartCommand,
                outputDirectory = part.OutputDirectory,
                framework = part.Framework,
                dockerfilePath = part.DockerfilePath,
                databaseEngine = part.DatabaseEngine
            }),
            confidence = plan.Confidence,
            plainSummary = plan.PlainSummary,
            clarifyingQuestion = plan.ClarifyingQuestion is null
                ? null
                : new
                {
                    prompt = plan.ClarifyingQuestion.Prompt,
                    options = plan.ClarifyingQuestion.Options.Select(option => new
                    {
                        id = option.Id,
                        label = option.Label,
                        description = option.Description,
                        resolvesToParts = option.ResolvesToParts.Select(part => new
                        {
                            role = part.Role,
                            providerName = part.ProviderName,
                            rootDirectory = part.RootDirectory,
                            serviceDirectory = part.ServiceDirectory,
                            buildCommand = part.BuildCommand,
                            installCommand = part.InstallCommand,
                            startCommand = part.StartCommand,
                            outputDirectory = part.OutputDirectory,
                            framework = part.Framework,
                            dockerfilePath = part.DockerfilePath,
                            databaseEngine = part.DatabaseEngine
                        })
                    })
                }
        });
    }

    /// <summary>
    /// Detects the frontend build profile for a given folder (Angular vs. plain package.json),
    /// reading <c>angular.json</c>/<c>package.json</c> to infer build/install commands and output dir.
    /// </summary>
    /// <param name="owner">Repo owner/org.</param>
    /// <param name="repo">Repo name.</param>
    /// <param name="path">Folder to inspect; empty/null inspects the repo root.</param>
    /// <param name="ref">Branch, tag, or commit SHA; null uses the repo's default branch.</param>
    [HttpGet("repos/{owner}/{repo}/build-profile")]
    public async Task<IActionResult> GetBuildProfile(
        string owner,
        string repo,
        [FromQuery] string? path,
        [FromQuery] string? @ref,
        CancellationToken cancellationToken)
    {
        var token = await GetGitHubTokenAsync(cancellationToken);
        var normalizedPath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Trim('/');
        var angularPath = string.IsNullOrEmpty(normalizedPath) ? "angular.json" : $"{normalizedPath}/angular.json";
        var packagePath = string.IsNullOrEmpty(normalizedPath) ? "package.json" : $"{normalizedPath}/package.json";

        var angularJson = await _gitHubService.GetFileContentAsync(token, owner, repo, angularPath, @ref, cancellationToken);
        var packageJson = await _gitHubService.GetFileContentAsync(token, owner, repo, packagePath, @ref, cancellationToken);
        var profile = _buildDetector.Detect(normalizedPath, angularJson, packageJson);

        return Ok(new
        {
            rootDirectory = profile.RootDirectory,
            buildCommand = profile.BuildCommand,
            installCommand = profile.InstallCommand,
            outputDirectory = profile.OutputDirectory,
            framework = profile.Framework
        });
    }

    /// <summary>
    /// Detects the backend build profile for a given folder (.NET/Node/Dockerfile-based), used to
    /// pre-fill the server deploy target's build/start commands and framework.
    /// </summary>
    /// <param name="owner">Repo owner/org.</param>
    /// <param name="repo">Repo name.</param>
    /// <param name="path">Folder to inspect; empty/null inspects the repo root.</param>
    /// <param name="ref">Branch, tag, or commit SHA; null uses the repo's default branch.</param>
    [HttpGet("repos/{owner}/{repo}/server-build-profile")]
    public async Task<IActionResult> GetServerBuildProfile(
        string owner,
        string repo,
        [FromQuery] string? path,
        [FromQuery] string? @ref,
        CancellationToken cancellationToken)
    {
        var token = await GetGitHubTokenAsync(cancellationToken);
        var normalizedPath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Trim('/');
        var profile = await _serverBuildProfileDiscovery.DiscoverAsync(
            token, owner, repo, normalizedPath, @ref, cancellationToken);

        return Ok(new
        {
            rootDirectory = profile.RootDirectory,
            buildCommand = profile.BuildCommand,
            installCommand = profile.InstallCommand,
            startCommand = profile.StartCommand,
            framework = profile.Framework,
            dockerfilePath = profile.DockerfilePath,
            serviceDirectory = profile.ServiceDirectory
        });
    }

    /// <summary>
    /// Detects whether a repo needs a Postgres and/or Redis database, by inspecting
    /// docker-compose files, appsettings.json connection strings, and a Prisma schema if present.
    /// </summary>
    /// <param name="owner">Repo owner/org.</param>
    /// <param name="repo">Repo name.</param>
    /// <param name="path">Server folder to inspect for appsettings.json; empty/null uses the repo root.</param>
    /// <param name="ref">Branch, tag, or commit SHA; null uses the repo's default branch.</param>
    [HttpGet("repos/{owner}/{repo}/database-requirements")]
    public async Task<IActionResult> GetDatabaseRequirements(
        string owner,
        string repo,
        [FromQuery] string? path,
        [FromQuery] string? @ref,
        CancellationToken cancellationToken)
    {
        var token = await GetGitHubTokenAsync(cancellationToken);
        var normalizedPath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Trim('/');

        var dockerCompose = await ReadFirstExistingFileAsync(
            token,
            owner,
            repo,
            ["docker-compose.yml", "docker-compose.yaml"],
            @ref,
            cancellationToken);
        var appsettingsPath = string.IsNullOrEmpty(normalizedPath)
            ? "appsettings.json"
            : $"{normalizedPath}/appsettings.json";
        var appsettings = await _gitHubService.GetFileContentAsync(
            token,
            owner,
            repo,
            appsettingsPath,
            @ref,
            cancellationToken);
        var prismaPaths = new List<string> { "prisma/schema.prisma" };
        if (!string.IsNullOrEmpty(normalizedPath))
        {
            prismaPaths.Add($"{normalizedPath}/prisma/schema.prisma");
        }

        var prismaSchema = await ReadFirstExistingFileAsync(
            token,
            owner,
            repo,
            prismaPaths,
            @ref,
            cancellationToken);

        var profile = _databaseRequirementDetector.Detect(dockerCompose, appsettings, prismaSchema);
        return Ok(new
        {
            requiresPostgres = profile.RequiresPostgres,
            requiresRedis = profile.RequiresRedis,
            connectionStringKeys = profile.ConnectionStringKeys,
            postgresDatabaseName = profile.PostgresDatabaseName
        });
    }

    private async Task<string?> ReadFirstExistingFileAsync(
        string token,
        string owner,
        string repo,
        IReadOnlyList<string> paths,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            var content = await _gitHubService.GetFileContentAsync(token, owner, repo, path, gitRef, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }
        }

        return null;
    }

    private async Task<string> GetGitHubTokenAsync(CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");
        var user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken);
        return _encryption.Decrypt(user.GitHubTokenEncrypted);
    }
}
