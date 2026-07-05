using System.Text.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider
{
    private async Task<IReadOnlyList<ProviderProject>> ListProjectsViaOAuthAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        var projects = new List<ProviderProject>();
        var workspaceIds = await ListAuthorizedWorkspaceIdsAsync(credentials, cancellationToken);
        foreach (var workspaceId in workspaceIds)
        {
            try
            {
                projects.AddRange(await ListProjectsInWorkspaceAsync(credentials, workspaceId, cancellationToken));
            }
            catch (DeployAIException)
            {
                // Try the next authorized workspace.
            }
        }

        if (projects.Count > 0)
        {
            return projects;
        }

        return await ListProjectsViaExternalWorkspacesAsync(credentials, cancellationToken);
    }

    private async Task<IReadOnlyList<ProviderProject>> ListProjectsInWorkspaceAsync(
        ProviderCredentials credentials,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        const string query = """
            query WorkspaceProjects($workspaceId: String!) {
              workspace(workspaceId: $workspaceId) {
                projects {
                  edges {
                    node {
                      id
                      name
                      environments {
                        edges {
                          node {
                            id
                            name
                          }
                        }
                      }
                      services {
                        edges {
                          node {
                            id
                            name
                          }
                        }
                      }
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
            new { workspaceId },
            cancellationToken);

        var projects = new List<ProviderProject>();
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("workspace", out var workspaceNode) ||
            !workspaceNode.TryGetProperty("projects", out var projectsNode))
        {
            return projects;
        }

        AppendProjectsFromProjectConnection(projects, projectsNode);
        return projects;
    }

    private async Task<IReadOnlyList<ProviderProject>> ListProjectsViaExternalWorkspacesAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        const string query = """
            query {
              externalWorkspaces {
                id
                name
                projects {
                  id
                  name
                  environments {
                    id
                    name
                  }
                  services {
                    id
                    name
                  }
                }
              }
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            null,
            cancellationToken);

        var projects = new List<ProviderProject>();
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("externalWorkspaces", out var workspacesNode) ||
            workspacesNode.ValueKind != JsonValueKind.Array)
        {
            return projects;
        }

        foreach (var workspaceNode in workspacesNode.EnumerateArray())
        {
            AppendProjectsFromWorkspaceNode(projects, workspaceNode, useConnectionShape: false);
        }

        return projects;
    }

    private async Task<IReadOnlyList<ProviderProject>> TryListRootProjectsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        const string query = """
            query {
              projects {
                edges {
                  node {
                    id
                    name
                    environments {
                      edges {
                        node {
                          id
                          name
                        }
                      }
                    }
                    services {
                      edges {
                        node {
                          id
                          name
                        }
                      }
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
            null,
            cancellationToken);

        var projects = new List<ProviderProject>();
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("projects", out var projectsNode) ||
            !projectsNode.TryGetProperty("edges", out var projectEdges))
        {
            return projects;
        }

        foreach (var projectEdge in projectEdges.EnumerateArray())
        {
            if (!projectEdge.TryGetProperty("node", out var projectNode))
            {
                continue;
            }

            AppendServicesFromProject(projects, projectNode);
        }

        return projects;
    }

    private static void AppendProjectsFromWorkspaceNode(
        List<ProviderProject> projects,
        JsonElement workspaceNode,
        bool useConnectionShape)
    {
        if (!workspaceNode.TryGetProperty("projects", out var projectsNode))
        {
            return;
        }

        if (useConnectionShape)
        {
            AppendProjectsFromProjectConnection(projects, projectsNode);
            return;
        }

        if (projectsNode.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var projectNode in projectsNode.EnumerateArray())
        {
            AppendServicesFromProject(projects, projectNode);
        }
    }

    private static void AppendProjectsFromProjectConnection(List<ProviderProject> projects, JsonElement projectsNode)
    {
        if (!projectsNode.TryGetProperty("edges", out var projectEdges))
        {
            return;
        }

        foreach (var projectEdge in projectEdges.EnumerateArray())
        {
            if (!projectEdge.TryGetProperty("node", out var projectNode))
            {
                continue;
            }

            AppendServicesFromProject(projects, projectNode);
        }
    }

    private static void AppendServicesFromProject(List<ProviderProject> projects, JsonElement projectNode)
    {
        var projectName = projectNode.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
        projectName ??= "Railway project";
        var environmentId = GetPreferredEnvironmentId(projectNode);
        if (environmentId is null)
        {
            return;
        }

        if (!projectNode.TryGetProperty("services", out var servicesNode))
        {
            return;
        }

        if (servicesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var serviceNode in servicesNode.EnumerateArray())
            {
                AddServiceProject(projects, projectName, environmentId, serviceNode);
            }

            return;
        }

        if (!servicesNode.TryGetProperty("edges", out var serviceEdges))
        {
            return;
        }

        foreach (var serviceEdge in serviceEdges.EnumerateArray())
        {
            if (!serviceEdge.TryGetProperty("node", out var serviceNode))
            {
                continue;
            }

            AddServiceProject(projects, projectName, environmentId, serviceNode);
        }
    }

    private static void AddServiceProject(
        List<ProviderProject> projects,
        string projectName,
        string environmentId,
        JsonElement serviceNode)
    {
        var serviceId = serviceNode.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
        var serviceName = serviceNode.TryGetProperty("name", out var serviceNameNode) ? serviceNameNode.GetString() : null;
        serviceName ??= "Service";
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return;
        }

        projects.Add(new ProviderProject(
            RailwayApiSupport.BuildProviderProjectId(serviceId, environmentId),
            $"{projectName} / {serviceName}",
            null));
    }
}
