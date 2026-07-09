namespace DeployAI.Core.Deployments;



public interface IDeploymentFixGenerator

{

    Task<IReadOnlyList<GeneratedDeploymentFile>> GenerateFixFilesAsync(

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


