using DeployAI.Core.Deployments;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Api.Services;

/// <summary>
/// What the code being deployed needs, against what the app it is deploying to actually has.
/// </summary>
/// <param name="Missing">Keys the code requires and the target does not have.</param>
/// <param name="Inconclusive">The comparison could not be made — never the same as "nothing
/// missing", because only one of those is safe to deploy on.</param>
public sealed record RequiredConfigurationResult(
    IReadOnlyList<string> Missing,
    bool Inconclusive,
    string? Message);

public interface IRequiredConfigurationCheck
{
    Task<RequiredConfigurationResult> CheckAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken);
}

/// <summary>
/// Compares the configuration the deployed ref requires against what the target has, before the
/// deploy rather than after it fails.
/// </summary>
/// <remarks>
/// Three incidents in one day, all the same shape and all found by something breaking rather than
/// by a check: an API crash-looped on `Jwt configuration missing` until Coolify gave up at ten
/// attempts; merging a branch that introduced a Media module needing `Storage:*` took every route
/// down when `AmazonS3Client` threw before `builder.Build()`; and `Tickets:SigningKey` 500'd every
/// Events request while the app looked healthy because the throw was inside a scoped factory.
/// <para>
/// The migration chain validated and the build was green in all three. Neither check can see this,
/// because the fault is not in the code or the schema — it is in the gap between them and the
/// target. DeployAI has both halves already: the resolver reads an app's real configuration files
/// wherever they live, and the provider listing says what the app actually has.
/// </para>
/// <para>
/// Advisory by design. "Required" here means a key the app declares with no value of its own, which
/// is a strong signal but not proof — a value can legitimately arrive from somewhere DeployAI cannot
/// see. Blocking a deploy on a guess would be worse than the failure it prevents; saying so before
/// the deploy is not.
/// </para>
/// </remarks>
public sealed class RequiredConfigurationCheck : IRequiredConfigurationCheck
{
    /// <summary>
    /// Supplied by the platform or the container, never by the user. Asking for these produces
    /// noise that trains people to ignore the warning, which costs more than the warning gains.
    /// </summary>
    private static readonly HashSet<string> SuppliedByTheRuntime = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "PORT", "TZ", "HOSTNAME", "PWD",
        "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "DOTNET_RUNNING_IN_CONTAINER",
        "NODE_ENV", "npm_config_cache"
    };

    private readonly DeployAIDbContext _db;
    private readonly IRepositoryLayoutResolver _layoutResolver;
    private readonly IRepositoryReader _reader;
    private readonly IEnvVarDetector _detector;
    private readonly IProviderManagementFactory _managementFactory;
    private readonly IProviderCredentialTokenService _tokens;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<RequiredConfigurationCheck> _logger;

    public RequiredConfigurationCheck(
        DeployAIDbContext db,
        IRepositoryLayoutResolver layoutResolver,
        IRepositoryReader reader,
        IEnvVarDetector detector,
        IProviderManagementFactory managementFactory,
        IProviderCredentialTokenService tokens,
        IEncryptionService encryption,
        ILogger<RequiredConfigurationCheck> logger)
    {
        _db = db;
        _layoutResolver = layoutResolver;
        _reader = reader;
        _detector = detector;
        _managementFactory = managementFactory;
        _tokens = tokens;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task<RequiredConfigurationResult> CheckAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken)
    {
        var parts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(serverTarget.ProviderProjectId))
        {
            return new RequiredConfigurationResult([], Inconclusive: false, null);
        }

        var user = await _db.Users.FirstAsync(u => u.Id == project.UserId, cancellationToken);
        var token = _encryption.Decrypt(user.GitHubTokenEncrypted);
        var config = DeployTargetConfig.Parse(serverTarget.ConfigJson);
        var serviceDir = config.ServiceDirectory ?? config.RootDirectory;

        var layout = await _layoutResolver.ResolveAsync(
            token, parts[0], parts[1], branch, serviceDir, cancellationToken);

        if (layout.IsInconclusive)
        {
            return Unknown($"could not read {project.GitHubRepoFullName}@{branch}");
        }

        var scan = _detector.Detect(new EnvScanInputs(
            AppsettingsContent: (await _reader.FindAsync(
                token, parts[0], parts[1], branch, layout, "appsettings.json", cancellationToken))?.Content,
            DotEnvExampleContent: (await _reader.FindAsync(
                token, parts[0], parts[1], branch, layout, ".env.example", cancellationToken))?.Content,
            ComposeContent: (await _reader.FindAsync(
                token, parts[0], parts[1], branch, layout, "docker-compose.yml", cancellationToken))?.Content));

        if (scan.IsInconclusive)
        {
            return Unknown($"found no configuration files under {layout.ProjectDirectory}");
        }

        // A key the app declares with no value of its own is one it expects to be given. A key that
        // carries a default already has an answer, and warning about it is noise.
        var required = scan.Variables
            .Where(v => !v.HasDefault && !SuppliedByTheRuntime.Contains(v.Name))
            .Select(v => v.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (required.Count == 0)
        {
            return new RequiredConfigurationResult([], Inconclusive: false, null);
        }

        IReadOnlyList<ProviderEnvVar> present;
        try
        {
            var management = _managementFactory.GetManagement(serverTarget.ProviderName)
                ?? throw new InvalidOperationException($"No management for {serverTarget.ProviderName}.");
            var providerToken = await _tokens.GetTokenAsync(serverTarget.Credential, cancellationToken);
            present = await management.ListEnvVarsAsync(
                new ProviderCredentials(providerToken), serverTarget.ProviderProjectId!, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the target's settings for project {ProjectId}.", project.Id);
            return Unknown("could not read what the app already has");
        }

        var have = present.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = required.Where(key => !have.Contains(key)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        if (missing.Count == 0)
        {
            return new RequiredConfigurationResult([], Inconclusive: false,
                $"Checked {required.Count} setting(s) this code needs: all present.");
        }

        // "Declares", not "needs". The first real run reported Storage__PublicBaseUrl, which the app
        // defines in its options class and never reads -- accurate as a statement about the
        // configuration, wrong as a prediction of failure. Saying "needs" and being wrong once
        // teaches people to skip the line the time it matters.
        var noun = missing.Count == 1 ? "setting" : "settings";
        return new RequiredConfigurationResult(
            missing,
            Inconclusive: false,
            $"This code declares {missing.Count} {noun} with no value, and the app has none set: "
            + $"{string.Join(", ", missing)}. It will still deploy — but if the app reads any of "
            + "them, it will fail once it starts. Add them on the app's settings screen.");
    }

    /// <summary>
    /// Said out loud rather than treated as "nothing missing". A comparison that could not be made
    /// is the case this check exists to stop passing silently.
    /// </summary>
    private static RequiredConfigurationResult Unknown(string reason) =>
        new([], Inconclusive: true,
            $"Could not check the settings this code needs — {reason}. "
            + "If it needs settings the app does not have, it will fail once it starts.");
}
