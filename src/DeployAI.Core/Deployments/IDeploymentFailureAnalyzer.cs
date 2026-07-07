namespace DeployAI.Core.Deployments;

public interface IDeploymentFailureAnalyzer
{
    DeploymentFailureAnalysis Analyze(string providerName, IReadOnlyList<string> logLines);

    /// <summary>Re-scans logs with higher limits so all extracted error lines reach the fix agent.</summary>
    DeploymentFailureAnalysis AnalyzeForFix(string providerName, IReadOnlyList<string> logLines);
}
