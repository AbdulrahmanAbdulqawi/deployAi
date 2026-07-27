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

    [JsonPropertyName("serviceDirectory")]
    public string? ServiceDirectory { get; set; }

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
        string.Equals(Role, "database", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this target is an actual deployable app (website/server), not a database.</summary>
    public bool IsDeployableTarget => !IsDatabaseTarget;

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
