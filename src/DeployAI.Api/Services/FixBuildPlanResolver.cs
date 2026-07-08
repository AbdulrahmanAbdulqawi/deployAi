using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services;

internal static class FixBuildPlanResolver
{
    internal static FixBuildPlan Resolve(
        string repoRoot,
        string providerName,
        DeployTargetConfig targetConfig,
        string? framework,
        DeploymentFailureAnalysis failureAnalysis)
    {
        string? dockerfileContent = null;
        var dockerfilePath = targetConfig.DockerfilePath;
        if (!string.IsNullOrWhiteSpace(dockerfilePath))
        {
            var fullDockerPath = Path.Combine(repoRoot, dockerfilePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullDockerPath))
            {
                dockerfileContent = File.ReadAllText(fullDockerPath);
            }
        }
        else
        {
            var defaultDocker = Path.Combine(repoRoot, "Dockerfile");
            if (File.Exists(defaultDocker))
            {
                dockerfileContent = File.ReadAllText(defaultDocker);
            }
        }

        var plan = FixBuildCommandResolver.Resolve(
            providerName,
            targetConfig,
            framework,
            failureAnalysis.ReferencedFiles,
            dockerfileContent);

        return FixBuildPlanRefiner.Refine(repoRoot, plan);
    }
}
