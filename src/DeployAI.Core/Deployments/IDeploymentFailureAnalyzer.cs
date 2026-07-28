namespace DeployAI.Core.Deployments;

/// <summary>Classifies a failed deployment's build/deploy logs into a <see cref="DeploymentFailureAnalysis"/>.</summary>
public interface IDeploymentFailureAnalyzer
{
    /// <summary>Analyzes logs for display in the UI (truncated error extraction).</summary>
    DeploymentFailureAnalysis Analyze(string providerName, IReadOnlyList<string> logLines);

    /// <summary>Re-scans logs with higher limits so all extracted error lines reach the fix agent.</summary>
    DeploymentFailureAnalysis AnalyzeForFix(string providerName, IReadOnlyList<string> logLines);
}
