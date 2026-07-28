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
    private readonly IEnvVarDetector _envVarDetector;
    private readonly IObjectStorageProviderFactory _storageFactory;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<GitHubController> _logger;

    public GitHubController(
        DeployAIDbContext db,
        ICurrentUserService currentUser,
        IGitHubService gitHubService,
        IFrontendBuildDetector buildDetector,
        IDatabaseRequirementDetector databaseRequirementDetector,
        IServerBuildProfileDiscovery serverBuildProfileDiscovery,
        IRepositoryClassifier repositoryClassifier,
        IEnvVarDetector envVarDetector,
        IObjectStorageProviderFactory storageFactory,
        IEncryptionService encryption,
        ILogger<GitHubController> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _gitHubService = gitHubService;
        _buildDetector = buildDetector;
        _databaseRequirementDetector = databaseRequirementDetector;
        _serverBuildProfileDiscovery = serverBuildProfileDiscovery;
        _repositoryClassifier = repositoryClassifier;
        _envVarDetector = envVarDetector;
        _storageFactory = storageFactory;
        _encryption = encryption;
        _logger = logger;
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
        _ = _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");

        // Coolify is the default target regardless of which credentials exist yet — the wizard
        // prompts for the connection afterwards. Previously this silently fell back to
        // Vercel/Railway whenever no Coolify credential was connected.
        var plan = await _repositoryClassifier.ClassifyAsync(
            token,
            owner,
            repo,
            @ref,
            new RepositoryClassificationOptions(),
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
    /// Detects the env vars this repo needs (from compose, .env.example, appsettings, README)
    /// and pairs each with a suggestion where the server knows a good value: generated secrets,
    /// the account email, and the connected storage account's details. Values for detected
    /// secrets are suggestions to *set*, never echoes of anything stored.
    /// </summary>
    [HttpGet("repos/{owner}/{repo}/env-schema")]
    public async Task<IActionResult> GetEnvSchema(
        string owner,
        string repo,
        [FromQuery] string? @ref,
        [FromQuery] string? composePath,
        [FromQuery] string? serverPath,
        CancellationToken cancellationToken)
    {
        var token = await GetGitHubTokenAsync(cancellationToken);
        var userId = _currentUser.UserId ?? throw new DeployAIException("unauthorized", "Sign in to continue.");

        var compose = await ReadFirstExistingFileAsync(
            token, owner, repo,
            [
                string.IsNullOrWhiteSpace(composePath) ? "docker-compose.coolify.yml" : composePath.Trim().TrimStart('/'),
                "docker-compose.coolify.yml",
                "docker-compose.yml"
            ],
            @ref, cancellationToken);
        var dotEnv = await ReadFirstExistingFileAsync(
            token, owner, repo, [".env.example", ".env.sample"], @ref, cancellationToken);
        var normalizedServerPath = string.IsNullOrWhiteSpace(serverPath) ? null : serverPath.Trim().Trim('/');
        var appsettings = await ReadFirstExistingFileAsync(
            token, owner, repo,
            normalizedServerPath is null
                ? ["appsettings.json"]
                : [$"{normalizedServerPath}/appsettings.json", "appsettings.json"],
            @ref, cancellationToken);
        var readme = await _gitHubService.GetFileContentAsync(token, owner, repo, "README.md", @ref, cancellationToken);

        var scan = _envVarDetector.Detect(new EnvScanInputs(
            ComposeContent: compose,
            DotEnvExampleContent: dotEnv,
            AppsettingsContent: appsettings,
            ReadmeContent: readme));

        var suggestions = await BuildEnvSuggestionsAsync(userId, scan.Variables, cancellationToken);

        if (scan.IsInconclusive)
        {
            _logger.LogWarning(
                "Env scan for {Owner}/{Repo} read no sources (serverPath: {ServerPath}); "
                + "an empty result here means the files were not found, not that the app needs no configuration.",
                owner,
                repo,
                normalizedServerPath ?? "(none)");
        }

        return Ok(new
        {
            vars = scan.Variables.Select(env => new
            {
                name = env.Name,
                isSecret = env.IsSecret,
                hasDefault = env.HasDefault,
                defaultValue = env.DefaultValue,
                category = env.Category.ToString().ToLowerInvariant(),
                sources = env.SeenIn,
                suggestedValue = suggestions.GetValueOrDefault(env.Name)
            }),
            // An empty vars list is ambiguous on its own, so the coverage travels with it: callers
            // can tell "this repo declares nothing" from "none of the files I read exist here", and
            // only the first is safe to deploy on without asking.
            scanned = scan.SourcesRead,
            notFound = scan.SourcesMissing,
            inconclusive = scan.IsInconclusive
        });
    }

    private async Task<Dictionary<string, string>> BuildEnvSuggestionsAsync(
        Guid userId,
        IReadOnlyList<Core.Deployments.Graph.DetectedEnvVar> detected,
        CancellationToken cancellationToken)
    {
        var suggestions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var env in detected.Where(e => e.IsSecret && !e.HasDefault))
        {
            // Long enough for JWT signing keys; users shouldn't have to invent secrets.
            // Passwords get every character class — ASP.NET Identity's default policy
            // rejects alphanumeric-only values ("at least one non alphanumeric character"),
            // and a generated password that can't boot the app is worse than none.
            var isPassword = env.Name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase);
            suggestions[env.Name] = isPassword ? GeneratePassword(20) : GenerateSecret(48);
        }

        if (detected.Any(e => e.Category == Core.Deployments.Graph.EnvVarCategory.AdminEmail))
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(user?.Email))
            {
                foreach (var env in detected.Where(e => e.Category == Core.Deployments.Graph.EnvVarCategory.AdminEmail))
                {
                    suggestions[env.Name] = user.Email;
                }
            }
        }

        var storageVars = detected.Where(e => e.Category == Core.Deployments.Graph.EnvVarCategory.Storage).ToList();
        if (storageVars.Count > 0)
        {
            await FillStorageSuggestionsAsync(userId, storageVars, suggestions, cancellationToken);
        }

        return suggestions;
    }

    /// <summary>
    /// Maps a connected storage account onto STORAGE_*/S3_* vars by suffix. Secret values are
    /// only ever suggested server→client over this authenticated response so the user can
    /// submit them back; nothing is written anywhere until they confirm the form.
    /// </summary>
    private async Task FillStorageSuggestionsAsync(
        Guid userId,
        IReadOnlyList<Core.Deployments.Graph.DetectedEnvVar> storageVars,
        Dictionary<string, string> suggestions,
        CancellationToken cancellationToken)
    {
        var credentials = await _db.ProviderCredentials
            .Where(c => c.UserId == userId && c.Kind == Data.Entities.CredentialKind.ObjectStorage)
            .ToListAsync(cancellationToken);

        // Only when unambiguous: guessing between two storage accounts is how keys for the
        // wrong bucket end up on an app.
        if (credentials.Count != 1)
        {
            return;
        }

        var payload = Core.Providers.StorageCredentialStorage.TryParse(_encryption.Decrypt(credentials[0].TokenEncrypted));
        if (payload is null)
        {
            return;
        }

        string? bucket = null;
        var provider = _storageFactory.GetObjectStorage(credentials[0].ProviderName);
        if (provider is not null)
        {
            try
            {
                var buckets = await provider.ListBucketsAsync(
                    new Core.Providers.ProviderCredentials(_encryption.Decrypt(credentials[0].TokenEncrypted)),
                    cancellationToken);
                bucket = buckets.Count == 1 ? buckets[0].Name : null;
            }
            catch
            {
                // Suggestion only — a flaky storage endpoint must not break schema detection.
            }
        }

        foreach (var env in storageVars)
        {
            var value = env.Name.ToUpperInvariant() switch
            {
                var n when n.EndsWith("ENDPOINT") => payload.Endpoint,
                var n when n.EndsWith("REGION") => payload.Region,
                var n when n.EndsWith("BUCKET") => bucket,
                var n when n.EndsWith("ACCESS_KEY") || n.EndsWith("ACCESSKEY") => payload.AccessKey,
                var n when n.EndsWith("SECRET_KEY") || n.EndsWith("SECRETKEY") => payload.SecretKey,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                suggestions[env.Name] = value;
            }
        }
    }

    private static string GenerateSecret(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(length, alphabet, static (span, chars) =>
        {
            Span<byte> bytes = stackalloc byte[span.Length];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[bytes[i] % chars.Length];
            }
        });
    }

    /// <summary>
    /// A password satisfying the strictest common policy: upper, lower, digit, and symbol.
    /// Symbols exclude anything docker compose / .env files treat specially ($, #, quotes,
    /// backslash) so the value survives Coolify's env interpolation verbatim.
    /// Public so tests can hold the policy in place — an alphanumeric-only password
    /// crash-looped a live deploy (ASP.NET Identity rejects it at seed time).
    /// </summary>
    public static string GeneratePassword(int length)
    {
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string digits = "0123456789";
        const string symbols = "!@^*-_+=?.";
        const string all = upper + lower + digits + symbols;

        Span<byte> bytes = stackalloc byte[length + 4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

        var result = new char[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = all[bytes[i] % all.Length];
        }

        // Guarantee one of each class at random-but-distinct positions.
        result[bytes[length] % length] = upper[bytes[length] % upper.Length];
        var positions = new HashSet<int> { bytes[length] % length };
        var classSets = new[] { lower, digits, symbols };
        for (var c = 0; c < classSets.Length; c++)
        {
            var pos = bytes[length + 1 + c] % length;
            while (positions.Contains(pos))
            {
                pos = (pos + 1) % length;
            }

            positions.Add(pos);
            result[pos] = classSets[c][bytes[length + 1 + c] % classSets[c].Length];
        }

        return new string(result);
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
