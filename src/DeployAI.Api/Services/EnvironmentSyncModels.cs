using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Api.Services;

public sealed record EnvironmentSyncOptions(
    bool RedeployRailwayAfterUpdate = false,
    bool RedeployVercelAfterUpdate = false,
    bool EnsureWebsiteWiring = true,
    bool ApplyVercelEnv = true,
    bool ApplyRailwayEnv = true,
    bool DetectDriftOnly = false,
    bool RunVerification = true,
    string Source = "manual");

public sealed record EnvironmentSyncResult(
    bool Success,
    bool DriftDetected,
    bool Skipped,
    string? SkipReason,
    string? ResolvedWebsiteUrl,
    string? ResolvedApiUrl,
    IReadOnlyList<string> RailwayKeysApplied,
    IReadOnlyList<string> VercelKeysApplied,
    IReadOnlyList<string> VerificationMessages,
    IReadOnlyList<string> DriftDetails,
    string Source,
    DateTimeOffset CompletedAt);

public sealed class ProjectEnvironmentSyncState
{
    public DateTimeOffset LastSyncedAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool DriftDetected { get; set; }
    public string? ResolvedWebsiteUrl { get; set; }
    public string? ResolvedApiUrl { get; set; }
    public List<string> VerificationMessages { get; set; } = [];
    public List<string> DriftDetails { get; set; } = [];

    public static ProjectEnvironmentSyncState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ProjectEnvironmentSyncState>(json);
    }

    public static ProjectEnvironmentSyncState FromResult(EnvironmentSyncResult result) =>
        new()
        {
            LastSyncedAt = result.CompletedAt,
            Source = result.Source,
            Success = result.Success,
            DriftDetected = result.DriftDetected,
            ResolvedWebsiteUrl = result.ResolvedWebsiteUrl,
            ResolvedApiUrl = result.ResolvedApiUrl,
            VerificationMessages = result.VerificationMessages.ToList(),
            DriftDetails = result.DriftDetails.ToList()
        };

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
}
