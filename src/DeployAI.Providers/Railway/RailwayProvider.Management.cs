using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider
{
    /// <summary>
    /// Creates a new Railway project and service for a GitHub repo, then best-effort applies any
    /// supplied build config (root directory, commands, Dockerfile) - a build-config failure here
    /// doesn't fail project creation, since settings can still be synced before the first deploy.
    /// </summary>
    public async Task<ProviderProject> CreateProjectAsync(
        ProviderCredentials credentials,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        var projectName = request.Name.Trim();
        var repo = RailwayApiSupport.NormalizeGitHubRepo(request.GitHubRepoFullName);
        var workspaceId = await ResolveWorkspaceIdAsync(credentials, cancellationToken);

        await using var gql = _graphQl.CreateSession(credentials);

        var projectResult = await gql.Client.CreateProject.ExecuteAsync(projectName, workspaceId, cancellationToken);
        var projectData = RailwayApiSupport.EnsureData(projectResult);
        var projectNode = projectData.ProjectCreate;
        var projectId = projectNode.Id
            ?? throw new InvalidOperationException("Railway returned an empty project id.");
        var environmentId = RailwayGraphQlMapping.GetPreferredEnvironmentId(
            RailwayGraphQlMapping.EnumerateEnvironmentEdges(
                projectNode.Environments?.Edges,
                edge => edge.Node is null ? null : (edge.Node.Id, edge.Node.Name)))
            ?? await FetchDefaultEnvironmentIdAsync(credentials, projectId, cancellationToken)
            ?? throw new InvalidOperationException("Railway did not return an environment for the new project.");

        var serviceResult = await gql.Client.CreateService.ExecuteAsync(
            new ServiceCreateInput
            {
                ProjectId = projectId,
                Name = projectName,
                EnvironmentId = environmentId,
                Source = new ServiceSourceInput { Repo = repo }
            },
            cancellationToken);
        var serviceData = RailwayApiSupport.EnsureData(serviceResult);
        var serviceNode = serviceData.ServiceCreate;
        var serviceId = serviceNode.Id
            ?? throw new InvalidOperationException("Railway returned an empty service id.");
        var serviceName = serviceNode.Name ?? projectName;

        if (!string.IsNullOrWhiteSpace(request.RootDirectory) ||
            !string.IsNullOrWhiteSpace(request.BuildCommand) ||
            !string.IsNullOrWhiteSpace(request.InstallCommand) ||
            !string.IsNullOrWhiteSpace(request.StartCommand) ||
            !string.IsNullOrWhiteSpace(request.ServiceDirectory) ||
            !string.IsNullOrWhiteSpace(request.DockerfilePath) ||
            string.Equals(request.Framework, "docker", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var environment = BuildEnvironmentFromCreateRequest(request);
                await ApplyBuildConfigurationAsync(
                    credentials,
                    serviceId,
                    environmentId,
                    environment,
                    cancellationToken);
            }
            catch (DeployAIException)
            {
                // Service was created; build settings can be synced on first deploy.
            }
            catch (InvalidOperationException)
            {
                // Service was created; build settings can be synced on first deploy.
            }
        }

        return new ProviderProject(
            RailwayApiSupport.BuildProviderProjectId(serviceId, environmentId),
            serviceName,
            null);
    }

    /// <summary>Falls back to a separate query for a new project's environment id when it wasn't included in the create-project response.</summary>
    private async Task<string?> FetchDefaultEnvironmentIdAsync(
        ProviderCredentials credentials,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.ProjectEnvironments.ExecuteAsync(projectId, cancellationToken);
        var data = RailwayApiSupport.TryGetData(result, static _ => false);
        if (data?.Project is null)
        {
            return null;
        }

        return RailwayGraphQlMapping.GetPreferredEnvironmentId(
            RailwayGraphQlMapping.EnumerateEnvironmentEdges(
                data.Project.Environments?.Edges,
                edge => edge.Node is null ? null : (edge.Node.Id, edge.Node.Name)));
    }

    public async Task DeleteProjectAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.DeleteProject.ExecuteAsync(providerProjectId, cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
    }
}
