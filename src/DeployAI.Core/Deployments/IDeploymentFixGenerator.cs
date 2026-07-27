namespace DeployAI.Core.Deployments;

/// <summary>Uses Claude to generate file changes that fix a failed deployment target or a specific failed verification check.</summary>
public interface IDeploymentFixGenerator

{

    /// <summary>Generates the fix files for a failed target (or, if <paramref name="verificationContext"/> is given, a specific failed check), reporting progress as it works.</summary>
    Task<IReadOnlyList<GeneratedDeploymentFile>> GenerateFixFilesAsync(

        Guid projectId,

        string owner,

        string repo,

        string gitRef,

        string githubAccessToken,

        string providerName,

        string? framework,

        DeployTargetConfig targetConfig,

        DeploymentFailureAnalysis failureAnalysis,

        Func<string, Task>? reportActivity,

        CancellationToken cancellationToken,

        DeploymentVerificationFixContext? verificationContext = null);

}


