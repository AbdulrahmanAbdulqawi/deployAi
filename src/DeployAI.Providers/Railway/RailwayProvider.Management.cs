using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider
{
    public async Task<ProviderProject> CreateProjectAsync(
        ProviderCredentials credentials,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        var projectName = request.Name.Trim();
        var repo = RailwayApiSupport.NormalizeGitHubRepo(request.GitHubRepoFullName);
        var workspaceId = await ResolveWorkspaceIdAsync(credentials, cancellationToken);

        const string createProjectMutation = """
            mutation CreateProject($name: String!, $workspaceId: String!) {
              projectCreate(input: { name: $name, workspaceId: $workspaceId }) {
                id
                environments {
                  edges {
                    node {
                      id
                      name
                    }
                  }
                }
              }
            }
            """;

        using var projectDocument = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            createProjectMutation,
            new { name = projectName, workspaceId },
            cancellationToken);

        var projectNode = projectDocument.RootElement
            .GetProperty("data")
            .GetProperty("projectCreate");
        var projectId = projectNode.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Railway returned an empty project id.");
        var environmentId = GetPreferredEnvironmentId(projectNode)
            ?? await FetchDefaultEnvironmentIdAsync(credentials, projectId, cancellationToken)
            ?? throw new InvalidOperationException("Railway did not return an environment for the new project.");

        const string createServiceMutation = """
            mutation CreateService($input: ServiceCreateInput!) {
              serviceCreate(input: $input) {
                id
                name
              }
            }
            """;

        using var serviceDocument = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            createServiceMutation,
            new
            {
                input = new
                {
                    projectId,
                    name = projectName,
                    environmentId,
                    source = new { repo }
                }
            },
            cancellationToken);

        var serviceNode = serviceDocument.RootElement
            .GetProperty("data")
            .GetProperty("serviceCreate");
        var serviceId = serviceNode.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Railway returned an empty service id.");
        var serviceName = serviceNode.GetProperty("name").GetString() ?? projectName;

        if (!string.IsNullOrWhiteSpace(request.RootDirectory) ||
            !string.IsNullOrWhiteSpace(request.BuildCommand) ||
            !string.IsNullOrWhiteSpace(request.InstallCommand) ||
            !string.IsNullOrWhiteSpace(request.DockerfilePath) ||
            string.Equals(request.Framework, "docker", StringComparison.OrdinalIgnoreCase))
        {
            try
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

    private async Task<string?> FetchDefaultEnvironmentIdAsync(
        ProviderCredentials credentials,
        string projectId,
        CancellationToken cancellationToken)
    {
        const string query = """
            query ProjectEnvironments($id: String!) {
              project(id: $id) {
                environments {
                  edges {
                    node {
                      id
                      name
                    }
                  }
                }
              }
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { id = projectId },
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("project", out var projectNode))
        {
            return null;
        }

        return GetPreferredEnvironmentId(projectNode);
    }
}
