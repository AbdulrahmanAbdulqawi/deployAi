using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Core.Deployments;

public sealed class DeployTargetConfig
{
    [JsonPropertyName("rootDirectory")]
    public string? RootDirectory { get; set; }

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

    [JsonPropertyName("databaseEngine")]
    public string? DatabaseEngine { get; set; }

    [JsonPropertyName("railwayProjectId")]
    public string? RailwayProjectId { get; set; }

    [JsonPropertyName("linkedServiceName")]
    public string? LinkedServiceName { get; set; }

    [JsonPropertyName("includePostgres")]
    public bool IncludePostgres { get; set; }

    [JsonPropertyName("includeRedis")]
    public bool IncludeRedis { get; set; }

    public bool IsDatabaseTarget =>
        string.Equals(Role, "database", StringComparison.OrdinalIgnoreCase);

    public bool IsDeployableTarget => !IsDatabaseTarget;

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

    public string ToJson() => JsonSerializer.Serialize(this);

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
            ServiceDirectory = profile.ServiceDirectory ?? profile.RootDirectory
        };

    public IReadOnlyDictionary<string, string> ToEnvironmentEntries()
    {
        var entries = new Dictionary<string, string>();
        AddEntry(entries, "dockerfilePath", DockerfilePath?.Trim().Trim('/'));
        AddEntry(entries, "serviceDirectory", ServiceDirectory?.Trim().Trim('/'));

        if (!string.IsNullOrWhiteSpace(DockerfilePath))
        {
            entries["rootDirectory"] = ".";
        }
        else
        {
            AddEntry(entries, "rootDirectory", RootDirectory?.Trim().Trim('/'));
        }

        AddEntry(entries, "outputDirectory", OutputDirectory?.Trim().Trim('/'));
        AddEntry(entries, "buildCommand", BuildCommand);
        AddEntry(entries, "installCommand", InstallCommand);
        AddEntry(entries, "startCommand", StartCommand);
        AddEntry(entries, "framework", Framework);
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
