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
    private readonly IRepositoryLayoutResolver _layoutResolver;
    private readonly IRepositoryReader _repositoryReader;
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
        IRepositoryLayoutResolver layoutResolver,
        IRepositoryReader repositoryReader,
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
        _layoutResolver = layoutResolver;
        _repositoryReader = repositoryReader;
        _storageFactory = storageFactory;
        _encryption = encryption;
        _logger = logger;
    }

    /// <summary>
    /// The nearest of several candidate filenames along the layout's search path. Tries each name in
    /// turn so a preferred variant (docker-compose.coolify.yml) wins over a fallback, at whatever
    /// depth it is found.
    /// </summary>
    private async Task<RepositoryFile?> FindNearestAsync(
        string token, string owner, string repo, string? gitRef,
        RepositoryLayout layout, string[] fileNames, CancellationToken cancellationToken)
    {
        foreach (var name in fileNames)
        {
            var found = await _repositoryReader.FindAsync(
                token, owner, repo, gitRef ?? string.Empty, layout, name, cancellationToken);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
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

        var normalizedServerPath = string.IsNullOrWhiteSpace(serverPath) ? null : serverPath.Trim().Trim('/');

        // Resolving the layout is what makes a nested app visible. Reading only the root and one
        // supplied path is why this scan returned nothing for a repository whose API lives at
        // backend/src/YemenHub.Api: appsettings.json was never at backend/src, so the wizard showed
        // no environment step and the API reached production crash-looping on missing Jwt settings.
        var layout = await _layoutResolver.ResolveAsync(
            token, owner, repo, @ref ?? string.Empty, normalizedServerPath, cancellationToken);

        // An explicit compose path is a caller's instruction, so it wins over the search.
        var compose = string.IsNullOrWhiteSpace(composePath)
            ? null
            : await _gitHubService.GetFileContentAsync(
                token, owner, repo, composePath.Trim().TrimStart('/'), @ref, cancellationToken);

        compose ??= (await FindNearestAsync(token, owner, repo, @ref, layout,
            ["docker-compose.coolify.yml", "docker-compose.yml"], cancellationToken))?.Content;
        var dotEnv = (await FindNearestAsync(token, owner, repo, @ref, layout,
            [".env.example", ".env.sample"], cancellationToken))?.Content;
        var appsettings = (await FindNearestAsync(token, owner, repo, @ref, layout,
            ["appsettings.json"], cancellationToken))?.Content;
        var readme = (await FindNearestAsync(token, owner, repo, @ref, layout,
            ["README.md"], cancellationToken))?.Content;

        var scan = _envVarDetector.Detect(new EnvScanInputs(
            ComposeContent: compose,
            DotEnvExampleContent: dotEnv,
            AppsettingsContent: appsettings,
            ReadmeContent: readme));

        var suggestions = await BuildEnvSuggestionsAsync(userId, scan.Variables, cancellationToken);

        // Two ways to be untrustworthy, and they are not the same. The layout being inconclusive
        // means the repository could not be listed at all; the scan being inconclusive means it was
        // listed but none of the files existed. Either makes an empty result unsafe to deploy on.
        var inconclusive = scan.IsInconclusive || layout.IsInconclusive;

        if (inconclusive)
        {
            _logger.LogWarning(
                "Env scan for {Owner}/{Repo} read no sources (serverPath: {ServerPath}, resolved to "
                + "{ProjectDirectory}, listed {DirectoryCount} directories); an empty result here means "
                + "the files were not found, not that the app needs no configuration.",
                owner,
                repo,
                normalizedServerPath ?? "(none)",
                layout.ProjectDirectory,
                layout.DirectoriesRead.Count);
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
                suggestedValue = suggestions.Values.GetValueOrDefault(env.Name),
                // Only set when the value came from something the user linked, so the form can say
                // "from your Coolify connection" instead of showing an unexplained prefilled box —
                // and can say what accepting it grants, where it grants anything.
                suggestedFrom = suggestions.Sources.GetValueOrDefault(env.Name)?.Source,
                suggestionExposure = suggestions.Sources.GetValueOrDefault(env.Name)?.Exposure
            }),
            // An empty vars list is ambiguous on its own, so the coverage travels with it: callers
            // can tell "this repo declares nothing" from "none of the files I read exist here", and
            // only the first is safe to deploy on without asking.
            scanned = scan.SourcesRead,
            notFound = scan.SourcesMissing,
            inconclusive,
            // Where the scan actually looked. Without this, a wrong answer and a right one are the
            // same shape, and the only way to tell them apart is to guess at the repository layout.
            projectDirectory = layout.ProjectDirectory,
            searchedIn = layout.SearchPath
        });
    }

    /// <summary>
    /// What to prefill, and — for anything that came from a linked connection rather than a random
    /// generator — where it came from, so a prefilled box is never an unexplained value.
    /// </summary>
    private sealed record EnvSuggestions(
        Dictionary<string, string> Values,
        Dictionary<string, EnvSuggestion> Sources);

    private async Task<EnvSuggestions> BuildEnvSuggestionsAsync(
        Guid userId,
        IReadOnlyList<Core.Deployments.Graph.DetectedEnvVar> detected,
        CancellationToken cancellationToken)
    {
        var suggestions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, EnvSuggestion>(StringComparer.OrdinalIgnoreCase);

        foreach (var env in detected.Where(e => e.IsSecret && !e.HasDefault && !IsUnguessableCredential(e.Name)))
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

        // Settings the user already answered by linking a connection. These run last so a real value
        // wins over a generated one: without that ordering, a *_COOLIFY_TOKEN is "secret with no
        // default", so the generator above invents a random string for it, and a random Coolify
        // token produces a container that starts, passes /health, and fails every real call.
        foreach (var (name, suggestion) in await BuildConnectionSuggestionsAsync(userId, detected, cancellationToken))
        {
            suggestions[name] = suggestion.Value;
            sources[name] = suggestion;
        }

        return new EnvSuggestions(suggestions, sources);
    }

    /// <summary>
    /// Values DeployAI is already holding, from connections this user has linked.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, EnvSuggestion>> BuildConnectionSuggestionsAsync(
        Guid userId,
        IReadOnlyList<Core.Deployments.Graph.DetectedEnvVar> detected,
        CancellationToken cancellationToken)
    {
        var names = detected.Select(env => env.Name).ToList();

        var coolify = await _db.ProviderCredentials
            .Where(c => c.UserId == userId &&
                        c.ProviderName == Core.Providers.ProviderNameValues.Coolify &&
                        c.Kind == Data.Entities.CredentialKind.Deployment)
            .ToListAsync(cancellationToken);

        // Only when unambiguous, for the same reason the storage suggestions are: picking between
        // two Coolify servers is how an app is handed a token for the wrong one.
        if (coolify.Count != 1)
        {
            return new Dictionary<string, EnvSuggestion>();
        }

        var payload = Core.Providers.CoolifyCredentialStorage.TryParse(_encryption.Decrypt(coolify[0].TokenEncrypted));
        return payload is null
            ? new Dictionary<string, EnvSuggestion>()
            : ConnectionEnvSuggestions.ForCoolify(payload.InstanceUrl, payload.ApiToken, names);
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

    /// <summary>
    /// A credential that has to match something outside this app, so inventing one is never right.
    /// </summary>
    /// <remarks>
    /// Generating is correct for a value the app only ever signs with — a JWT key, a ticket
    /// signature — because nothing else has to agree with it. It is actively harmful for a
    /// credential that authenticates somewhere else: a private key has a public half held by whoever
    /// issued it, and a random 48 characters cannot be that half. Observed on Mirqab, whose
    /// MIRQAB_GITHUB_PRIVATE_KEY arrived at the form prefilled with a generated string. That deploys
    /// a container that starts, answers /health, and fails every GitHub call it makes — the failure
    /// shape this codebase has spent a long time removing. An empty box is the honest answer.
    /// </remarks>
    private static bool IsUnguessableCredential(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper.EndsWith("PRIVATE_KEY", StringComparison.Ordinal) ||
               upper.EndsWith("PRIVATEKEY", StringComparison.Ordinal);
    }

    private static string GenerateSecret(int length) => GeneratedSecrets.Secret(length);

    /// <summary>
    /// Delegates to <see cref="GeneratedSecrets.Password"/>. Kept here, and public, because tests
    /// hold the password policy in place through this name.
    /// </summary>
    public static string GeneratePassword(int length) => GeneratedSecrets.Password(length);

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
