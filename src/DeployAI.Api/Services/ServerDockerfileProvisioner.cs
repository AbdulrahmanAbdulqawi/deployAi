using Microsoft.Extensions.Logging;
using DeployAI.Infrastructure.Adapters;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Api.Services;

/// <summary>
/// Ensures a .NET server that Coolify's Nixpacks can't build (a modular monolith, or a .NET
/// version newer than Nixpacks' SDK) has a Dockerfile committed to the repo, so the app can be
/// created with the Dockerfile build pack instead. The Dockerfile is generated deterministically
/// and committed to the deployment branch as an additive artifact (idempotent: re-running upserts
/// it), so redeploys always build the current code.
/// </summary>
public interface IServerDockerfileProvisioner
{
    Task<ServerDockerfileResult?> EnsureDockerfileAsync(
        string githubToken,
        string owner,
        string repo,
        string branch,
        string rootDirectory,
        string? serviceDirectory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Same idea for a server-rendered frontend, for a different reason: Coolify's Nixpacks build
    /// receives only its own NIXPACKS_*/COOLIFY_* variables, never the app's, so values the
    /// framework inlines at build time (NEXT_PUBLIC_*) never reach the bundle and it keeps the
    /// source's localhost fallback. Owning the Dockerfile is what lets them be passed as build args.
    /// </summary>
    /// <param name="framework">What the target is configured as. Used only to confirm a package.json
    /// found somewhere other than the configured directory really belongs to this website — a repo
    /// root holding both a backend and a frontend must not have its API's manifest picked up.</param>
    Task<ServerDockerfileResult?> EnsureSsrWebsiteDockerfileAsync(
        string githubToken,
        string owner,
        string repo,
        string branch,
        string appDirectory,
        string? framework,
        IReadOnlyList<string> buildTimeEnvKeys,
        string? buildCommand,
        string? startCommand,
        string? installCommand,
        CancellationToken cancellationToken);
}

/// <summary>The build directory (Coolify base directory) and the Dockerfile path relative to it.</summary>
public sealed record ServerDockerfileResult(string BaseDirectory, string DockerfileLocation, int ExposedPort);

public sealed class ServerDockerfileProvisioner : IServerDockerfileProvisioner
{
    private readonly IGitHubService _gitHubService;
    private readonly IRepositoryLayoutResolver _layoutResolver;
    private readonly IFrontendBuildDetector _frontendDetector;
    private readonly ILogger<ServerDockerfileProvisioner> _logger;

    public ServerDockerfileProvisioner(
        IGitHubService gitHubService,
        IRepositoryLayoutResolver layoutResolver,
        IFrontendBuildDetector frontendDetector,
        ILogger<ServerDockerfileProvisioner> logger)
    {
        _gitHubService = gitHubService;
        _layoutResolver = layoutResolver;
        _frontendDetector = frontendDetector;
        _logger = logger;
    }

    public async Task<ServerDockerfileResult?> EnsureDockerfileAsync(
        string githubToken,
        string owner,
        string repo,
        string branch,
        string rootDirectory,
        string? serviceDirectory,
        CancellationToken cancellationToken)
    {
        var buildRoot = Normalize(rootDirectory);
        var serviceDir = Normalize(serviceDirectory ?? rootDirectory);

        // The shared resolver finds the entry project, descending a level when the service directory
        // holds projects rather than being one -- the modular monolith this generator exists for. It
        // judges a csproj by its SDK attribute, so the entry point is the web project and not
        // whichever library happened to sort first.
        var layout = await _layoutResolver.ResolveAsync(
            githubToken, owner, repo, branch, serviceDir, cancellationToken);

        var csprojPath = layout.EntryProjectPath is { } path &&
                         path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;

        if (csprojPath is null)
        {
            // Never silent: returning null here means the app deploys on whatever build pack it
            // already had, and the reason has to be visible or it looks like nothing was attempted.
            _logger.LogWarning(
                "No entry .csproj found under {ServiceDirectory} in {Owner}/{Repo}@{Branch}; "
                + "leaving the existing build configuration alone.",
                serviceDir,
                owner,
                repo,
                branch);
            return null;
        }

        // The project directory is wherever the csproj actually is, which is not necessarily the
        // directory we were told to look in -- and the publish path is built relative to the build
        // root, so a stale value here publishes "YemenHub.Api.csproj" instead of
        // "YemenHub.Api/YemenHub.Api.csproj" and the build fails to find the project.
        serviceDir = layout.ProjectDirectory;

        var csprojContent = await _gitHubService.GetFileContentAsync(
            githubToken, owner, repo, csprojPath, branch, cancellationToken);

        var csprojName = csprojPath[(csprojPath.LastIndexOf('/') + 1)..];
        var dockerfileContent = DotnetServerDockerfile.Build(buildRoot, serviceDir, csprojContent, csprojName);
        var dockerfilePath = string.IsNullOrEmpty(buildRoot) ? "Dockerfile" : $"{buildRoot}/Dockerfile";

        // Idempotent: if a Dockerfile is already there, update it (needs the blob sha).
        var existing = await _gitHubService.GetFileMetadataAsync(
            githubToken, owner, repo, dockerfilePath, branch, cancellationToken);

        // Unchanged means no commit. The website path had this and the server path did not, so every
        // redeploy of a .NET app appended a commit to the user's history that changed nothing.
        if (existing is not null && ContentMatches(existing.Content, dockerfileContent))
        {
            return new ServerDockerfileResult(
                BaseDirectory: buildRoot,
                DockerfileLocation: "/Dockerfile",
                ExposedPort: DotnetServerDockerfile.ContainerPort);
        }

        await _gitHubService.UpsertFileAsync(
            githubToken,
            owner,
            repo,
            dockerfilePath,
            dockerfileContent,
            existing is null
                ? "Add Dockerfile for Coolify deployment (generated by DeployAI)"
                : "Update Dockerfile for Coolify deployment (generated by DeployAI)",
            branch,
            existing?.Sha,
            cancellationToken);

        // Coolify's dockerfile_location is relative to the base directory (the build root).
        return new ServerDockerfileResult(
            BaseDirectory: buildRoot,
            DockerfileLocation: "/Dockerfile",
            ExposedPort: DotnetServerDockerfile.ContainerPort);
    }

    public async Task<ServerDockerfileResult?> EnsureSsrWebsiteDockerfileAsync(
        string githubToken,
        string owner,
        string repo,
        string branch,
        string appDirectory,
        string? framework,
        IReadOnlyList<string> buildTimeEnvKeys,
        string? buildCommand,
        string? startCommand,
        string? installCommand,
        CancellationToken cancellationToken)
    {
        var configured = Normalize(appDirectory);

        // The same resolver the server side uses. This read only the configured directory, so a
        // frontend one level down -- a repo whose website target points at "apps" and whose Next.js
        // app is "apps/web" -- found no package.json, returned null, and the site silently stayed on
        // Nixpacks. That is precisely the failure this generator exists to prevent, arriving through
        // the scan instead of the generator.
        var layout = await _layoutResolver.ResolveAsync(
            githubToken, owner, repo, branch, configured, cancellationToken);

        if (layout.IsInconclusive)
        {
            // Never silent: leaving the site on Nixpacks because nobody could read the repository is
            // a different fact from leaving it there because it is not a Node app.
            _logger.LogWarning(
                "Could not read {Owner}/{Repo}@{Branch} under '{Directory}', so the website stays on "
                + "its current build pack; whether it needs a generated Dockerfile is unknown.",
                owner, repo, branch, configured);
            return null;
        }

        // The build context is the directory holding the package.json the image installs.
        var appDir = layout.ProjectDirectory;
        var packageJsonPath = string.IsNullOrEmpty(appDir) ? "package.json" : $"{appDir}/package.json";
        var packageJson = await _gitHubService.GetFileContentAsync(
            githubToken, owner, repo, packageJsonPath, branch, cancellationToken);
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            // Not a Node app. Correct to do nothing, and worth saying where it looked -- "no
            // package.json" without a path is unactionable when the answer is a wrong directory.
            _logger.LogInformation(
                "No package.json under [{SearchPath}] in {Owner}/{Repo}@{Branch}; "
                + "leaving the website on its current build pack.",
                string.Join(", ", layout.SearchPath.Select(p => string.IsNullOrEmpty(p) ? "<repo root>" : p)),
                owner, repo, branch);
            return null;
        }

        // Descending is only safe with evidence. A website target pointed at a directory that holds
        // both an API and a frontend would otherwise take whichever manifest was listed first and
        // build the wrong half -- worse than the Nixpacks build it replaced. Staying put needs no
        // check: that directory is what the user chose.
        if (!string.Equals(appDir, configured, StringComparison.OrdinalIgnoreCase) &&
            !IsTheConfiguredFramework(packageJson, framework))
        {
            _logger.LogInformation(
                "The package.json found at '{Found}' is not the {Framework} app this target deploys; "
                + "leaving the website on its current build pack.",
                appDir,
                framework ?? "configured");
            return null;
        }

        var lockPath = string.IsNullOrEmpty(appDir) ? "package-lock.json" : $"{appDir}/package-lock.json";
        var hasLockfile = !string.IsNullOrWhiteSpace(await _gitHubService.GetFileContentAsync(
            githubToken, owner, repo, lockPath, branch, cancellationToken));

        var dockerfileContent = SsrFrontendDockerfile.Build(
            packageJson,
            hasLockfile,
            buildTimeEnvKeys,
            buildCommand,
            startCommand,
            installCommand);

        var dockerfilePath = string.IsNullOrEmpty(appDir) ? "Dockerfile" : $"{appDir}/Dockerfile";
        var existing = await _gitHubService.GetFileMetadataAsync(
            githubToken, owner, repo, dockerfilePath, branch, cancellationToken);

        // Idempotent, and skipped entirely when the content already matches so redeploys don't
        // pile up no-op commits on the branch.
        if (existing is not null && ContentMatches(existing.Content, dockerfileContent))
        {
            return new ServerDockerfileResult(appDir, "/Dockerfile", SsrFrontendDockerfile.ContainerPort);
        }

        await _gitHubService.UpsertFileAsync(
            githubToken,
            owner,
            repo,
            dockerfilePath,
            dockerfileContent,
            existing is null
                ? "Add Dockerfile for the website build (generated by DeployAI)"
                : "Update Dockerfile for the website build (generated by DeployAI)",
            branch,
            existing?.Sha,
            cancellationToken);

        return new ServerDockerfileResult(appDir, "/Dockerfile", SsrFrontendDockerfile.ContainerPort);
    }

    /// <summary>
    /// Whether a manifest found away from the configured directory is the framework this target
    /// deploys, judged by the dependencies it declares rather than by where it sits.
    /// </summary>
    /// <remarks>
    /// Deliberately strict: an unrecognised manifest is treated as "not it". Doing nothing leaves
    /// the site on the build pack it already had, which is the behaviour this method has always had
    /// for a directory with no package.json — building the wrong application would be new damage.
    /// </remarks>
    private bool IsTheConfiguredFramework(string packageJson, string? framework)
    {
        if (string.IsNullOrWhiteSpace(framework))
        {
            return false;
        }

        var detected = _frontendDetector.Detect(string.Empty, null, packageJson).Framework;
        return detected is not null &&
               (string.Equals(detected, framework, StringComparison.OrdinalIgnoreCase) ||
                // "nextjs" and "next" name the same framework; the config carries whichever the
                // wizard wrote, the detector always answers with its own spelling.
                string.Equals($"{detected}js", framework, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(detected, $"{framework}js", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the file already in the repository is byte-for-byte what would be written, ignoring
    /// only line-ending style.
    /// </summary>
    /// <remarks>
    /// This used to base64-decode its argument first, because GitHub's contents API returns files
    /// encoded. <see cref="IGitHubService.GetFileMetadataAsync"/> already decodes, so it was decoding
    /// plain text: every call threw <see cref="FormatException"/>, was swallowed, and returned false.
    /// The check therefore never once matched, and the "skip when unchanged" it guards never once
    /// ran — four consecutive deploys of one app produced four commits that changed nothing, in a
    /// user's own history, all sharing one message. A caught exception that returns the unsafe
    /// answer is invisible exactly the way this codebase keeps being bitten by.
    /// </remarks>
    private static bool ContentMatches(string? existingContent, string desired) =>
        !string.IsNullOrEmpty(existingContent) &&
        string.Equals(
            existingContent.Replace("\r\n", "\n").TrimEnd(),
            desired.Replace("\r\n", "\n").TrimEnd(),
            StringComparison.Ordinal);

    /// <summary>
    /// Looks in each immediate subdirectory for a csproj, and returns the one that is a web project.
    /// A modular monolith puts its entry project beside the libraries it references, and only the
    /// entry uses the Web SDK -- picking the first csproj found would build a class library.
    /// </summary>
    private static string Normalize(string? path) =>
        path?.Trim().Replace('\\', '/').Trim('/') ?? string.Empty;
}
