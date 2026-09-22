using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Infrastructure.Adapters;

/// <summary>
/// Angular as a plugin. Wraps the Angular branch of <see cref="FrontendBuildDetector"/> for
/// detection/build knowledge and generates the nginx-serving Dockerfile that previously lived
/// in Dockerfile.web.tpl — so nothing outside this class needs to know Angular exists.
/// </summary>
public sealed class AngularAdapter : IFrameworkAdapter
{
    private readonly FrontendBuildDetector _detector = new();

    public string Id => "angular";

    /// <summary>A built bundle has no server of its own; health is the proxy's concern.</summary>
    public string? HealthPath => null;

    public EnvKeyConvention EnvConvention => EnvKeyConvention.FlatUpperSnake;

    public AdapterDetection? Detect(RepositorySignals signals)
    {
        var framework = FrontendBuildDetector.DetectFramework(signals.AngularJson, signals.PackageJson);
        if (!string.Equals(framework, "angular", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // angular.json is unambiguous; @angular/core alone is nearly so. Either way this claim
        // must outrank the generic Node adapter looking at the same package.json.
        return new AdapterDetection(
            Confidence: signals.AngularJson is not null ? 0.95 : 0.9,
            Capabilities: ServiceCapability.StaticSite);
    }

    public ServiceNode CreateServiceNode(string serviceId, RepositorySignals signals)
    {
        var profile = _detector.Detect(signals.Directory, signals.AngularJson, signals.PackageJson);
        var buildCommand = string.IsNullOrWhiteSpace(profile.BuildCommand) ? "npm run build" : profile.BuildCommand;
        var outputDirectory = profile.OutputDirectory?.Trim().Trim('/') ?? "dist/app/browser";

        // The final stage serves through nginx so the node can also act as the deployment's
        // reverse proxy — the single-origin transform adds that capability and the generator
        // writes the nginx.conf beside this Dockerfile. A repo that brought its own is respected
        // instead, the same way every other adapter does it.
        var dockerfile = signals.HasDockerfile
            ? new DockerfileSpec(Content: null, ExistingPath: "Dockerfile", ExposedPort: StaticBundleDockerfile.ExposedPort)
            : new DockerfileSpec(
                StaticBundleDockerfile.Render(profile.RootDirectory, buildCommand, outputDirectory),
                ExistingPath: null,
                ExposedPort: StaticBundleDockerfile.ExposedPort);

        return new ServiceNode(
            serviceId,
            ServiceCapability.StaticSite,
            ServiceSource.FromBuild(profile.RootDirectory, dockerfile),
            Framework: Id,
            Ports: [StaticBundleDockerfile.ExposedPort]);
    }
}
