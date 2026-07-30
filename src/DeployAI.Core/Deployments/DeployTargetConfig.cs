using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Core.Deployments;

/// <summary>
/// The parsed shape of a <c>DeployTarget.ConfigJson</c> blob - build/start commands, framework,
/// role ("website"/"server"/"database"), and Railway-specific database linkage. Every layer that
/// reads or writes a deploy target's config goes through <see cref="Parse"/>/<see cref="ToJson"/>
/// rather than touching the raw JSON string directly.
/// </summary>
public sealed class DeployTargetConfig
{
    [JsonPropertyName("rootDirectory")]
    public string? RootDirectory { get; set; }

    /// <summary>"website", "server", or "database" - see <see cref="IsDatabaseTarget"/>/<see cref="IsDeployableTarget"/>.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("outputDirectory")]
    public string? OutputDirectory { get; set; }

    [JsonPropertyName("buildCommand")]
    public string? BuildCommand { get; set; }

    [JsonPropertyName("installCommand")]
    public string? InstallCommand { get; set; }

    [JsonPropertyName("framework")]
    public string? Framework { get; set; }

    [JsonPropertyName("startCommand")]
    public string? StartCommand { get; set; }

    [JsonPropertyName("dockerfilePath")]
    public string? DockerfilePath { get; set; }

    /// <summary>
    /// The port the generated image listens on, recorded when DeployAI selects the Dockerfile build
    /// so a later config sync re-sends it rather than inferring one from the framework name.
    /// </summary>
    public string? ExposedPort { get; set; }

    [JsonPropertyName("serviceDirectory")]
    public string? ServiceDirectory { get; set; }

    /// <summary>
    /// The compose file this target deploys, set only when the whole app is one Docker Compose
    /// resource rather than one application per role.
    /// </summary>
    /// <remarks>
    /// A single-origin compose plan has a website part and a server part, but they become one
    /// Coolify application — so only one deploy target is created, and it is given the website's
    /// role because that is the half facing the browser. Nothing used to record that the target
    /// was a compose deployment at all, and every later reader re-derived the shape from a lone
    /// website target: readiness could not tell it needed a compose file, and the SSR provisioner
    /// switched the application off compose onto a Dockerfile build of the client alone. The plan
    /// was right, the persistence was lossy, and the last writer guessed.
    /// </remarks>
    [JsonPropertyName("composeFileLocation")]
    public string? ComposeFileLocation { get; set; }

    /// <summary>The compose service the domain attaches to; the rest stay on the internal network.</summary>
    [JsonPropertyName("domainServiceName")]
    public string? DomainServiceName { get; set; }

    /// <summary>
    /// Where the server half lives, carried on the website-role target because the compose plan's
    /// server part never becomes a target of its own. Readiness needs it to know which directory
    /// must hold the API's Dockerfile.
    /// </summary>
    [JsonPropertyName("composeServerDirectory")]
    public string? ComposeServerDirectory { get; set; }

    /// <summary>The server half's framework, carried for the same reason as <see cref="ComposeServerDirectory"/>.</summary>
    [JsonPropertyName("composeServerFramework")]
    public string? ComposeServerFramework { get; set; }

    /// <summary>
    /// Whether this target is one Docker Compose resource hosting several services, rather than a
    /// single application built from a build pack.
    /// </summary>
    [JsonIgnore]
    public bool IsComposeTarget => !string.IsNullOrWhiteSpace(ComposeFileLocation);

    /// <summary>Whether a Docker build should use the repo root as build context even though <see cref="RootDirectory"/> points at a subfolder.</summary>
    [JsonPropertyName("dockerUsesRepositoryRoot")]
    public bool DockerUsesRepositoryRoot { get; set; }

    /// <summary>"postgres" or "redis", set only on database-role targets.</summary>
    [JsonPropertyName("databaseEngine")]
    public string? DatabaseEngine { get; set; }

    /// <summary>The Railway project a linked database service lives in, set only on database-role targets.</summary>
    [JsonPropertyName("railwayProjectId")]
    public string? RailwayProjectId { get; set; }

    /// <summary>The Railway service name a database-role target's connection info was linked from.</summary>
    [JsonPropertyName("linkedServiceName")]
    public string? LinkedServiceName { get; set; }

    [JsonPropertyName("includePostgres")]
    public bool IncludePostgres { get; set; }

    [JsonPropertyName("includeRedis")]
    public bool IncludeRedis { get; set; }

    /// <summary>Whether this target represents a database (Postgres/Redis) rather than a deployable app.</summary>
    public bool IsDatabaseTarget =>
        string.Equals(Role, DeploymentPartRoles.Database, StringComparison.OrdinalIgnoreCase);

    public bool IsStorageTarget =>
        string.Equals(Role, DeploymentPartRoles.Storage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Databases and buckets are provisioned, not deployed — nothing is built or published for
    /// them, so they must stay out of deploy loops and progress bars.
    /// </summary>
    public bool IsDeployableTarget => !IsDatabaseTarget && !IsStorageTarget;

    /// <summary>Parses a target's ConfigJson, returning an empty config (not throwing) for null/empty/invalid JSON.</summary>
    public static DeployTargetConfig Parse(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson) || configJson == "{}")
        {
            return new DeployTargetConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<DeployTargetConfig>(configJson) ?? new DeployTargetConfig();
        }
        catch (JsonException)
        {
            return new DeployTargetConfig();
        }
    }

    /// <summary>Serializes this config back to the JSON stored in <c>DeployTarget.ConfigJson</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Builds a database-role config linking to a provisioned Railway database service.</summary>
    public static DeployTargetConfig FromDatabaseService(
        string databaseEngine,
        string railwayProjectId,
        string linkedServiceName) =>
        new()
        {
            Role = "database",
            DatabaseEngine = databaseEngine,
            RailwayProjectId = railwayProjectId,
            LinkedServiceName = linkedServiceName
        };

    /// <summary>Builds a config from a detected frontend build profile (used for website-role targets).</summary>
    public static DeployTargetConfig FromProfile(FrontendBuildProfile profile, string role) =>
        new()
        {
            RootDirectory = profile.RootDirectory,
            Role = role,
            OutputDirectory = profile.OutputDirectory,
            BuildCommand = profile.BuildCommand,
            InstallCommand = profile.InstallCommand,
            Framework = profile.Framework
        };

    /// <summary>Builds a config from a detected backend build profile (used for server-role targets).</summary>
    public static DeployTargetConfig FromServerProfile(ServerBuildProfile profile, string role) =>
        new()
        {
            RootDirectory = profile.RootDirectory,
            Role = role,
            BuildCommand = profile.BuildCommand,
            InstallCommand = profile.InstallCommand,
            StartCommand = profile.StartCommand,
            Framework = profile.Framework,
            DockerfilePath = profile.DockerfilePath,
            ServiceDirectory = profile.ServiceDirectory ?? profile.RootDirectory,
            DockerUsesRepositoryRoot = profile.DockerUsesRepositoryRoot
        };

    /// <summary>Flattens the non-empty build-relevant fields into a string dictionary, for passing as deploy-time environment entries.</summary>
    public IReadOnlyDictionary<string, string> ToEnvironmentEntries()
    {
        var entries = new Dictionary<string, string>();
        AddEntry(entries, "dockerfilePath", DockerfilePath?.Trim().Trim('/'));
        AddEntry(entries, "exposedPort", ExposedPort?.Trim());
        AddEntry(entries, "serviceDirectory", ServiceDirectory?.Trim().Trim('/'));
        if (DockerUsesRepositoryRoot)
        {
            entries["dockerUsesRepositoryRoot"] = "true";
        }

        AddEntry(entries, "rootDirectory", RootDirectory?.Trim().Trim('/'));
        if (!entries.ContainsKey("rootDirectory") && !string.IsNullOrWhiteSpace(DockerfilePath))
        {
            entries["rootDirectory"] = ".";
        }

        AddEntry(entries, "outputDirectory", OutputDirectory?.Trim().Trim('/'));
        AddEntry(entries, "buildCommand", BuildCommand);
        AddEntry(entries, "installCommand", InstallCommand);
        AddEntry(entries, "startCommand", StartCommand);
        AddEntry(entries, "framework", Framework);
        AddEntry(entries, "role", Role);
        return entries;
    }

    private static void AddEntry(Dictionary<string, string> entries, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            entries[key] = value;
        }
    }
}
