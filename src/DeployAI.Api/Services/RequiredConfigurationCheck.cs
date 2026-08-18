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
/// <param name="UnconfiguredSections">Configuration sections the code sets up for development and
/// the target has no setting from at all.</param>
public sealed record RequiredConfigurationResult(
    IReadOnlyList<string> Missing,
    bool Inconclusive,
    string? Message,
    IReadOnlyList<string>? UnconfiguredSections = null);

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

    /// <summary>
    /// Runs the check and records what it learned, so the same question can be asked again later
    /// without re-reading the repository.
    /// </summary>
    /// <remarks>
    /// The capture is a wrapper rather than a step inside the check because the check has several
    /// exits, and a manifest that is only written on the happy path would leave the sweep unable to
    /// tell "this target has no required settings" from "the last deploy never got far enough to
    /// find out" — the same absence confusion this check exists to prevent one level down.
    /// </remarks>
    public async Task<RequiredConfigurationResult> CheckAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        CancellationToken cancellationToken)
    {
        var capture = new ManifestCapture(project.Id, serverTarget.Id, branch);
        var result = await EvaluateAsync(project, serverTarget, branch, capture, cancellationToken);

        try
        {
            await PersistManifestAsync(capture, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The manifest is for the next sweep's benefit; failing to store it must not fail the
            // deploy-time answer that was already computed correctly.
            _logger.LogWarning(
                ex, "Could not record the configuration manifest for target {TargetId}.", serverTarget.Id);
        }

        return result;
    }

    private async Task<RequiredConfigurationResult> EvaluateAsync(
        Project project,
        DeployTarget serverTarget,
        string branch,
        ManifestCapture capture,
        CancellationToken cancellationToken)
    {
        var parts = project.GitHubRepoFullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(serverTarget.ProviderProjectId))
        {
            capture.Blind("this app has no repository or no application recorded");
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
            capture.Blind($"could not read {project.GitHubRepoFullName}@{branch}");
            return Unknown($"could not read {project.GitHubRepoFullName}@{branch}");
        }

        var appsettings = (await _reader.FindAsync(
            token, parts[0], parts[1], branch, layout, "appsettings.json", cancellationToken))?.Content;

        // Read for its section names only -- see UnconfiguredSections below.
        var developmentAppsettings = (await _reader.FindAsync(
            token, parts[0], parts[1], branch, layout, "appsettings.Development.json", cancellationToken))?.Content;

        var scan = _detector.Detect(new EnvScanInputs(
            AppsettingsContent: appsettings,
            DotEnvExampleContent: (await _reader.FindAsync(
                token, parts[0], parts[1], branch, layout, ".env.example", cancellationToken))?.Content,
            ComposeContent: (await _reader.FindAsync(
                token, parts[0], parts[1], branch, layout, "docker-compose.yml", cancellationToken))?.Content));

        if (scan.IsInconclusive)
        {
            capture.Blind($"found no configuration files under {layout.ProjectDirectory}");
            return Unknown($"found no configuration files under {layout.ProjectDirectory}");
        }

        // A key the app declares with no value of its own is one it expects to be given. A key that
        // carries a default already has an answer, and warning about it is noise.
        var required = scan.Variables
            .Where(v => !v.HasDefault && !SuppliedByTheRuntime.Contains(v.Name))
            .Select(v => v.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        capture.Requires(required);

        var developmentOnlySections = SectionsOnlyIn(developmentAppsettings, appsettings);

        if (required.Count == 0 && developmentOnlySections.Count == 0)
        {
            // Conclusively nothing required. Recorded as such, so the sweep can distinguish it from a
            // target whose manifest was never captured.
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
            capture.Blind("could not read what the app already has");
            return Unknown("could not read what the app already has");
        }

        capture.Observed(present);

        var have = present.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = required.Where(key => !have.Contains(key)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        // A whole section the app has nothing from. Deliberately section-level rather than key-level:
        // an app with Jwt__SigningKey set clearly has its Jwt section configured, and naming the
        // leaves it does not carry (Issuer, Audience) would be noise -- those usually have defaults
        // in the options class, which DeployAI cannot see. Nothing at all from the section is the
        // shape of the incident.
        var unconfigured = developmentOnlySections
            .Where(section => !have.Any(key => key.StartsWith($"{section}__", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0 && unconfigured.Count == 0)
        {
            return new RequiredConfigurationResult([], Inconclusive: false,
                $"Checked {required.Count} setting(s) this code needs: all present.");
        }

        if (missing.Count == 0)
        {
            return new RequiredConfigurationResult([], Inconclusive: false,
                UnconfiguredSectionsMessage(unconfigured), unconfigured);
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
            + "them, it will fail once it starts. Add them on the app's settings screen."
            + (unconfigured.Count > 0 ? " " + UnconfiguredSectionsMessage(unconfigured) : string.Empty),
            unconfigured);
    }

    /// <summary>
    /// Configuration sections an environment-specific appsettings file sets up and the base file
    /// does not mention at all.
    /// </summary>
    /// <remarks>
    /// The gap this closes was found by reading the check's own blind spot on the repository it was
    /// written for. yemenConnect's <c>appsettings.json</c> has no <c>Jwt</c> section — it lives only
    /// in <c>appsettings.Development.json</c>, along with <c>Tickets</c> and <c>Bootstrap</c>. So the
    /// check built to catch a crash-loop on "Jwt configuration missing" could not see that the app
    /// had a Jwt section, and would have reported nothing about the exact incident that motivated it.
    /// <para>
    /// Only names are taken from the development file, never values. A development signing key is
    /// not a production default — the file is not loaded when <c>ASPNETCORE_ENVIRONMENT</c> is
    /// Production — so treating its value as "this key has an answer" is how a secret that must be
    /// supplied looks like one that is already handled.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> SectionsOnlyIn(string? developmentJson, string? baseJson)
    {
        var development = TopLevelSections(developmentJson);
        if (development.Count == 0)
        {
            return [];
        }

        var basic = TopLevelSections(baseJson);
        return development
            .Where(section => !basic.Contains(section))
            .Where(section => !SectionsTheRuntimeOwns.Contains(section))
            .ToList();
    }

    /// <summary>Sections that are framework plumbing, not application configuration.</summary>
    private static readonly HashSet<string> SectionsTheRuntimeOwns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Logging", "AllowedHosts", "Kestrel", "ConnectionStrings"
    };

    private static IReadOnlyList<string> TopLevelSections(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return [];
            }

            return document.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                .Select(p => p.Name)
                .ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static string UnconfiguredSectionsMessage(IReadOnlyList<string> sections)
    {
        var noun = sections.Count == 1 ? "a section" : "sections";
        var verb = sections.Count == 1 ? "is" : "are";
        return $"This code also configures {noun} in appsettings.Development.json that the app has "
            + $"nothing set from: {string.Join(", ", sections)}. Development values {verb} not "
            + "defaults for production — that file is not loaded when the app runs.";
    }

    /// <summary>
    /// Said out loud rather than treated as "nothing missing". A comparison that could not be made
    /// is the case this check exists to stop passing silently.
    /// </summary>
    private static RequiredConfigurationResult Unknown(string reason) =>
        new([], Inconclusive: true,
            $"Could not check the settings this code needs — {reason}. "
            + "If it needs settings the app does not have, it will fail once it starts.");

    /// <summary>
    /// Writes the manifest for this target, replacing whatever the previous deploy recorded.
    /// </summary>
    /// <remarks>
    /// Replaced rather than appended: the question the sweep asks is "does the running app still have
    /// what the deployed code needs", and only the most recent deploy defines that. Keeping older
    /// manifests would invite comparing against code that is no longer running.
    /// </remarks>
    private async Task PersistManifestAsync(ManifestCapture capture, CancellationToken cancellationToken)
    {
        var manifest = await _db.TargetConfigManifests
            .FirstOrDefaultAsync(m => m.DeployTargetId == capture.DeployTargetId, cancellationToken);

        if (manifest is null)
        {
            manifest = new TargetConfigManifest { DeployTargetId = capture.DeployTargetId };
            _db.TargetConfigManifests.Add(manifest);
        }

        manifest.ProjectId = capture.ProjectId;
        manifest.Branch = capture.Branch;
        manifest.WasInconclusive = capture.IsBlind;
        manifest.InconclusiveReason = capture.BlindReason;
        manifest.CapturedAt = DateTimeOffset.UtcNow;
        manifest.SetRequiredKeys(capture.RequiredKeys);
        manifest.SetValueFingerprints(capture.Fingerprints(capture.DeployTargetId));

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Accumulates what the check learned on its way through, so every exit records something.
    /// </summary>
    private sealed class ManifestCapture
    {
        private readonly Dictionary<string, string> _observedValues = new(StringComparer.OrdinalIgnoreCase);

        public ManifestCapture(Guid projectId, Guid deployTargetId, string branch)
        {
            ProjectId = projectId;
            DeployTargetId = deployTargetId;
            Branch = branch;
        }

        public Guid ProjectId { get; }
        public Guid DeployTargetId { get; }
        public string Branch { get; }
        public bool IsBlind { get; private set; }
        public string? BlindReason { get; private set; }
        public List<string> RequiredKeys { get; } = [];

        /// <summary>The scan did not get far enough to know what the code requires.</summary>
        public void Blind(string reason)
        {
            IsBlind = true;
            BlindReason = reason;
        }

        public void Requires(IEnumerable<string> keys) => RequiredKeys.AddRange(keys);

        /// <summary>
        /// Records the target's current values for the required keys, so a later change is visible.
        /// </summary>
        /// <remarks>
        /// Values the provider withholds (<c>ValueHidden</c>) are deliberately not recorded. A
        /// fingerprint of a blank placeholder would match forever and report "unchanged" about a
        /// value nobody ever read — a check that always passes is worse than no check.
        /// </remarks>
        public void Observed(IEnumerable<ProviderEnvVar> present)
        {
            var required = RequiredKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var variable in present)
            {
                if (!required.Contains(variable.Key) || variable.ValueHidden ||
                    string.IsNullOrEmpty(variable.Value))
                {
                    continue;
                }

                _observedValues[variable.Key] = variable.Value;
            }
        }

        public Dictionary<string, string> Fingerprints(Guid deployTargetId) =>
            _observedValues.ToDictionary(
                pair => pair.Key,
                pair => ConfigValueFingerprint.Compute(deployTargetId, pair.Key, pair.Value),
                StringComparer.OrdinalIgnoreCase);
    }
}
