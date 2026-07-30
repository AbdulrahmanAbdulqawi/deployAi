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
