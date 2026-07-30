using System.Text.Json;
using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>
/// Makes a server-rendered frontend on Coolify build from a DeployAI-generated Dockerfile instead
/// of Nixpacks.
/// <para>
/// Nixpacks builds receive only Coolify's own NIXPACKS_*/COOLIFY_* variables, never the
/// application's. Frameworks in this family compile public values into the bundle at build time —
/// Next.js inlines NEXT_PUBLIC_*, Vite inlines VITE_* — so the build bakes in the source's
/// fallback (usually a localhost URL) and the deployed site calls the developer's machine. Nothing
/// about that is visible in the build log or fixable by setting an environment variable.
/// </para>
/// <para>
/// This runs before the deploy so the switch applies to apps that already exist, keeping their
/// domain, rather than requiring the app to be recreated.
/// </para>
/// </summary>
public interface ISsrWebsiteBuildProvisioner
{
    Task EnsureAsync(Project project, DeployTarget websiteTarget, string branch, CancellationToken cancellationToken);
}

public sealed class SsrWebsiteBuildProvisioner : ISsrWebsiteBuildProvisioner
{
    private readonly DeployAIDbContext _db;
    private readonly IServerDockerfileProvisioner _dockerfileProvisioner;
    private readonly Providers.Coolify.CoolifyProvider _coolifyProvider;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<SsrWebsiteBuildProvisioner> _logger;

    public SsrWebsiteBuildProvisioner(
        DeployAIDbContext db,
        IServerDockerfileProvisioner dockerfileProvisioner,
        Providers.Coolify.CoolifyProvider coolifyProvider,
        IProviderCredentialTokenService tokens,
        IEncryptionService encryption,
        ILogger<SsrWebsiteBuildProvisioner> logger)
    {
        _db = db;
        _dockerfileProvisioner = dockerfileProvisioner;
        _coolifyProvider = coolifyProvider;
        _tokens = tokens;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task EnsureAsync(
        Project project,
        DeployTarget websiteTarget,
        string branch,
        CancellationToken cancellationToken)
    {
        var config = DeployTargetConfig.Parse(websiteTarget.ConfigJson);
        if (!SsrFrontendFrameworks.Inlines(config.Framework))
        {
            return;
        }

        var parts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(websiteTarget.ProviderProjectId))
        {
            return;
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var githubToken = _encryption.Decrypt(user.GitHubTokenEncrypted);
        var appDirectory = config.ServiceDirectory ?? config.RootDirectory ?? string.Empty;

        var provisioned = await _dockerfileProvisioner.EnsureSsrWebsiteDockerfileAsync(
            githubToken,
            parts[0],
            parts[1],
            branch,
            appDirectory,
            config.Framework,
            ResolveBuildTimeEnvKeys(project, config.Framework),
            config.BuildCommand,
            config.StartCommand,
            config.InstallCommand,
            cancellationToken);

        if (provisioned is null)
        {
            // The provisioner says why, and which directories it read — a bare "no package.json"
            // here restated the guess that was wrong in the first place.
            return;
        }

        var token = await _tokens.GetTokenAsync(websiteTarget.Credential, cancellationToken);

        // Belt as well as braces. Everything below aims to build from the generated Dockerfile,
        // which pins its own Node — but if any of it fails, or Coolify falls back for a reason of
        // its own, the build lands on Nixpacks' default of Node 18. That is older than Angular,
        // Next and Vite accept, so an app whose package.json declares no engines fails on a version
        // nobody chose. Coolify prints the fix in the build log and waits for a human to go and set
        // this variable; a platform that reads that log can set it itself.
        await EnsureNixpacksNodeVersionAsync(
            new ProviderCredentials(token), websiteTarget, provisioned.NodeMajor, cancellationToken);

        var switched = await _coolifyProvider.ConfigureDockerfileBuildAsync(
            new ProviderCredentials(token),
            websiteTarget.ProviderProjectId,
            provisioned.BaseDirectory,
            provisioned.DockerfileLocation,
            provisioned.ExposedPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);

        if (!switched)
        {
            return;
        }

        // Record it so the plan and the branch-mismatch warnings describe what actually builds.
        config.DockerfilePath = provisioned.DockerfileLocation;
        var configJson = config.ToJson();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE deploy_targets SET "ConfigJson" = {configJson} WHERE "Id" = {websiteTarget.Id}""",
            cancellationToken);

        // And onto the instance the rest of this deploy reads, which the row above is not.
        //
        // Without this the switch above was undone seconds later, every time. The caller re-parses
        // this entity to build the configuration it pushes to Coolify before triggering the build;
        // with DockerfilePath still missing from it, that push resolved the build pack to Nixpacks
        // and overwrote the Dockerfile that had just been generated, committed and selected. The
        // deploy then built an Angular 20 app on Nixpacks' default Node 18 and failed on the
        // version check — three correct steps erased by one that was written only to the database.
        websiteTarget.ConfigJson = configJson;
        var tracked = project.DeployTargets.FirstOrDefault(t => t.Id == websiteTarget.Id);
        if (tracked is not null)
        {
            tracked.ConfigJson = configJson;
        }
    }

    /// <summary>
    /// Tells Coolify which Node major to use if it ever builds this app with Nixpacks.
    /// </summary>
    /// <remarks>
    /// Advisory in the strongest sense: it must not stop a deploy. The variable only matters on a
    /// path this method is trying to avoid, so failing the deploy because it could not be written
    /// would trade a possible problem for a certain one.
    /// </remarks>
    private async Task EnsureNixpacksNodeVersionAsync(
        ProviderCredentials credentials,
        DeployTarget websiteTarget,
        int? nodeMajor,
        CancellationToken cancellationToken)
    {
        if (nodeMajor is not { } major)
        {
            return;
        }

        try
        {
            await _coolifyProvider.UpsertEnvVarAsync(
                credentials,
                websiteTarget.ProviderProjectId!,
                new UpsertProviderEnvVarRequest(
                    "NIXPACKS_NODE_VERSION",
                    major.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ProviderEnvVarTypes.Plain,
                    []),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not set NIXPACKS_NODE_VERSION on website target {TargetId}; continuing.",
                websiteTarget.Id);
        }
    }

    /// <summary>
    /// The keys the build has to see. Two sources, because they are populated by different things:
    /// what the user manages for the project, and the API URL keys DeployAI derives itself once the
    /// server has a domain. The generator keeps only the public, bundle-inlined ones so no secret
    /// becomes a build arg.
    /// <para>
    /// Reading only the stored set is what made this feature miss the one variable it exists for.
    /// The API URL is never typed into the managed-environment screen — <see
    /// cref="FrontendEnvironmentWiringService"/> computes it from the server's domain and writes it
    /// straight to the provider — so it was never in the project's stored variables, no
    /// <c>ARG NEXT_PUBLIC_API_URL</c> was emitted, and the bundle kept its localhost fallback. The
    /// generated Dockerfile looked correct and the deploy succeeded; only the browser saw it.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> ResolveBuildTimeEnvKeys(Project project, string? framework)
    {
        // Derived from the framework rather than read back from the provider, so the list does not
        // depend on the wiring having already run for this app.
        var keys = new List<string>(CrossProviderUrlWiring.ResolveApiEnvKeys(framework));

        if (project.EnvironmentVariablesEncrypted is { Length: > 0 })
        {
            try
            {
                var stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    _encryption.Decrypt(project.EnvironmentVariablesEncrypted));
                if (stored is not null)
                {
                    keys.AddRange(stored.Keys);
                }
            }
            catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException)
            {
                _logger.LogWarning(ex, "Could not read stored environment variables for project {ProjectId}.", project.Id);
            }
        }

        return keys.Distinct(StringComparer.Ordinal).ToList();
    }
}
