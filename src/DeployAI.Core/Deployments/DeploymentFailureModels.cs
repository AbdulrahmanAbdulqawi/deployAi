using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Core.Deployments;

public enum DeploymentFailureCategory
{
    CodeBuild,
    Infrastructure,
    Unknown
}

public sealed record DeploymentFailureAnalysis(
    DeploymentFailureCategory Category,
    string Summary,
    string? ErrorExcerpt,
    IReadOnlyList<string> ReferencedFiles,
    bool CanRequestClaudeFix,
    int ErrorCount = 0);

public sealed record DeploymentFixResult(
    string BranchName,
    int PullRequestNumber,
    string PullRequestUrl,
    IReadOnlyList<string> CommittedFiles);

public static class DeploymentFailureAnalysisJson
{
    public static DeploymentFailureAnalysis? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var state = JsonSerializer.Deserialize<DeploymentFailureAnalysisState>(json);
        if (state is null)
        {
            return null;
        }

        var category = state.Category?.ToLowerInvariant() switch
        {
            "code_build" => DeploymentFailureCategory.CodeBuild,
            "infrastructure" => DeploymentFailureCategory.Infrastructure,
            _ => DeploymentFailureCategory.Unknown
        };

        return new DeploymentFailureAnalysis(
            category,
            state.Summary ?? string.Empty,
            state.ErrorExcerpt,
            state.ReferencedFiles ?? [],
            state.CanRequestClaudeFix,
            state.ErrorCount);
    }

    public static string ToJson(DeploymentFailureAnalysis analysis) =>
        JsonSerializer.Serialize(new DeploymentFailureAnalysisState
        {
            Category = analysis.Category switch
            {
                DeploymentFailureCategory.CodeBuild => "code_build",
                DeploymentFailureCategory.Infrastructure => "infrastructure",
                _ => "unknown"
            },
            Summary = analysis.Summary,
            ErrorExcerpt = analysis.ErrorExcerpt,
            ReferencedFiles = analysis.ReferencedFiles.ToList(),
            CanRequestClaudeFix = analysis.CanRequestClaudeFix,
            ErrorCount = analysis.ErrorCount
        }, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

    private sealed class DeploymentFailureAnalysisState
    {
        public string? Category { get; set; }
        public string? Summary { get; set; }
        public string? ErrorExcerpt { get; set; }
        public List<string>? ReferencedFiles { get; set; }
        public bool CanRequestClaudeFix { get; set; }
        public int ErrorCount { get; set; }
    }
}
