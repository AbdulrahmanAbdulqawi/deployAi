using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider
{
    private const string DockerfilePathVariable = "RAILWAY_DOCKERFILE_PATH";

    private static Dictionary<string, object?> BuildSettingsFromEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        var input = new Dictionary<string, object?>();
        var isDotnet = environment.TryGetValue("framework", out var framework) &&
                       string.Equals(framework, "dotnet", StringComparison.OrdinalIgnoreCase);
        var isDocker = string.Equals(framework, "docker", StringComparison.OrdinalIgnoreCase);
        var hasMonorepoDockerfile = environment.TryGetValue("dockerfilePath", out var dockerfilePath) &&
                                    !string.IsNullOrWhiteSpace(dockerfilePath);

        if (hasMonorepoDockerfile)
        {
            var (dockerRootDirectory, _) = ResolveDockerBuildPaths(environment);
            input["rootDirectory"] = dockerRootDirectory;
        }
        else if (isDocker || environment.TryGetValue("rootDirectory", out var rootDirectory))
        {
            var resolvedRoot = ResolveRootDirectory(environment);
            if (!string.IsNullOrWhiteSpace(resolvedRoot))
            {
                input["rootDirectory"] = resolvedRoot;
            }
        }

        if ((!isDotnet || ShouldApplyDotnetBuildCommands(environment)) &&
            !isDocker &&
            environment.TryGetValue("buildCommand", out var buildCommand) &&
            !string.IsNullOrWhiteSpace(buildCommand))
        {
            input["buildCommand"] = buildCommand;
        }

        if ((!isDotnet || ShouldApplyDotnetBuildCommands(environment)) &&
            !isDocker &&
            environment.TryGetValue("startCommand", out var startCommand) &&
            !string.IsNullOrWhiteSpace(startCommand))
        {
            input["startCommand"] = startCommand;
        }

        return input;
    }

    internal static Dictionary<string, string> BuildEnvironmentFromCreateRequest(CreateProviderProjectRequest request)
    {
        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(request.RootDirectory))
        {
            environment["rootDirectory"] = request.RootDirectory;
        }

        if (!string.IsNullOrWhiteSpace(request.Framework))
        {
            environment["framework"] = request.Framework;
        }

        if (!string.IsNullOrWhiteSpace(request.DockerfilePath))
        {
            environment["dockerfilePath"] = request.DockerfilePath;
        }

        if (!string.IsNullOrWhiteSpace(request.BuildCommand))
        {
            environment["buildCommand"] = request.BuildCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.InstallCommand))
        {
            environment["installCommand"] = request.InstallCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.ServiceDirectory))
        {
            environment["serviceDirectory"] = request.ServiceDirectory;
        }

        if (!string.IsNullOrWhiteSpace(request.StartCommand))
        {
            environment["startCommand"] = request.StartCommand;
        }

        return environment;
    }

    private static Dictionary<string, object?> BuildSettingsFromCreateRequest(CreateProviderProjectRequest request) =>
        BuildSettingsFromEnvironment(BuildEnvironmentFromCreateRequest(request));

    private static (string RootDirectory, string? DockerfilePath) ResolveDockerBuildPaths(
        IReadOnlyDictionary<string, string> environment)
    {
        environment.TryGetValue("dockerfilePath", out var dockerfilePath);
        environment.TryGetValue("serviceDirectory", out var serviceDirectory);

        var normalizedDockerfilePath = dockerfilePath?.Trim().Trim('/') ?? string.Empty;
        var normalizedServiceDirectory = serviceDirectory?.Trim().Trim('/') ?? string.Empty;

        if (!string.IsNullOrEmpty(normalizedServiceDirectory) &&
            string.Equals(
                normalizedDockerfilePath,
                $"{normalizedServiceDirectory}/Dockerfile",
                StringComparison.OrdinalIgnoreCase) &&
            environment.TryGetValue("dockerUsesRepositoryRoot", out var usesRepositoryRoot) &&
            string.Equals(usesRepositoryRoot, "false", StringComparison.OrdinalIgnoreCase))
        {
            return (normalizedServiceDirectory, "Dockerfile");
        }

        if (environment.TryGetValue("dockerUsesRepositoryRoot", out var repositoryRootFlag) &&
            string.Equals(repositoryRootFlag, "true", StringComparison.OrdinalIgnoreCase))
        {
            return (
                ".",
                string.IsNullOrWhiteSpace(normalizedDockerfilePath) ? null : normalizedDockerfilePath);
        }

        return (
            ResolveRootDirectory(environment),
            string.IsNullOrWhiteSpace(normalizedDockerfilePath) ? null : normalizedDockerfilePath);
    }

    private static string ResolveRootDirectory(IReadOnlyDictionary<string, string> environment)
    {
        if (!environment.TryGetValue("rootDirectory", out var rootDirectory) ||
            string.IsNullOrWhiteSpace(rootDirectory) ||
            string.Equals(rootDirectory, ".", StringComparison.Ordinal))
        {
            return ".";
        }

        return rootDirectory.Trim().Trim('/');
    }

    private static bool IsDotnetMonorepoLayout(IReadOnlyDictionary<string, string> environment)
    {
        if (!environment.TryGetValue("serviceDirectory", out var serviceDirectory) ||
            string.IsNullOrWhiteSpace(serviceDirectory))
        {
            return false;
        }

        var normalizedService = serviceDirectory.Trim().Trim('/');
        var normalizedRoot = ResolveRootDirectory(environment);
        return !string.Equals(normalizedRoot, normalizedService, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldApplyDotnetBuildCommands(IReadOnlyDictionary<string, string> environment)
    {
        if (IsDotnetMonorepoLayout(environment))
        {
            return true;
        }

        return environment.TryGetValue("buildCommand", out var buildCommand) &&
               buildCommand.Contains("dotnet publish", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ApplyBuildConfigurationAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var settings = BuildSettingsFromEnvironment(environment);
        if (settings.Count > 0)
        {
            await ApplyServiceSettingsAsync(credentials, serviceId, environmentId, settings, cancellationToken);
        }

        var (_, resolvedDockerfilePath) = ResolveDockerBuildPaths(environment);
        if (!string.IsNullOrWhiteSpace(resolvedDockerfilePath))
        {
            await EnsureServiceVariableAsync(
                credentials,
                serviceId,
                environmentId,
                DockerfilePathVariable,
                resolvedDockerfilePath,
                cancellationToken);
        }
    }

    private async Task ApplyServiceSettingsAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        Dictionary<string, object?> settings,
        CancellationToken cancellationToken)
    {
        if (settings.Count == 0)
        {
            return;
        }

        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.UpdateServiceInstance.ExecuteAsync(
            serviceId,
            environmentId,
            RailwayGraphQlMapping.ToServiceInstanceUpdateInput(settings),
            cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
    }

    private async Task EnsureServiceVariableAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);

        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.VariableUpsert.ExecuteAsync(
            new VariableUpsertInput
            {
                ProjectId = projectId,
                EnvironmentId = environmentId,
                ServiceId = serviceId,
                Name = name,
                Value = value,
                SkipDeploys = true
            },
            cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
    }
}
