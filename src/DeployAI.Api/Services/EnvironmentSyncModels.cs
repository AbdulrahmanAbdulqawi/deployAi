using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Api.Services;

/// <summary>Flags controlling what a cross-provider environment sync run actually does (drift-only vs. apply, whether to redeploy, whether to run live verification).</summary>
public sealed record EnvironmentSyncOptions(
    bool RedeployRailwayAfterUpdate = false,
    bool RedeployVercelAfterUpdate = false,
    bool EnsureWebsiteWiring = true,
    bool ApplyVercelEnv = true,
    bool ApplyRailwayEnv = true,
    bool DetectDriftOnly = false,
    bool RunVerification = true,
    string Source = "manual");

/// <summary>The outcome of a cross-provider environment sync run - resolved URLs, which env var keys were applied, drift found, and verification results.</summary>
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

/// <summary>The last environment sync result for a project, persisted as JSON on <c>Project.EnvironmentSyncJson</c> so it survives across requests without re-running the sync.</summary>
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

    /// <summary>Parses a project's stored sync-state JSON, returning null if none is stored yet.</summary>
    public static ProjectEnvironmentSyncState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ProjectEnvironmentSyncState>(json);
    }

    /// <summary>Builds the persisted state from a fresh sync result.</summary>
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
