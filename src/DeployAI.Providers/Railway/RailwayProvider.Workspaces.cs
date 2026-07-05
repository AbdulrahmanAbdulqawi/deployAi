using System.Text.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider
{
    private async Task<string> ResolveWorkspaceIdAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        var workspaceIds = await ListAuthorizedWorkspaceIdsAsync(credentials, cancellationToken);
        var workspaceId = workspaceIds.FirstOrDefault();
        if (workspaceId is not null)
        {
            return workspaceId;
        }

        throw new DeployAIException(
            "railway_workspace_missing",
            "We couldn't find a Railway workspace for this connection. Disconnect Railway in Connections, connect again, and choose at least one workspace on the consent screen.");
    }

    private async Task<string> ResolveWorkspaceIdForProjectAsync(
        ProviderCredentials credentials,
        string projectId,
        string? workspaceIdHint,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(workspaceIdHint))
        {
            return workspaceIdHint;
        }

        const string projectQuery = """
            query ProjectWorkspace($id: String!) {
              project(id: $id) {
                workspaceId
              }
            }
            """;

        try
        {
            using var document = await RailwayApiSupport.ExecuteAsync(
                _httpClient,
                credentials.Token,
                projectQuery,
                new { id = projectId },
                cancellationToken);

            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("project", out var projectNode) &&
                projectNode.TryGetProperty("workspaceId", out var workspaceNode))
            {
                var workspaceId = workspaceNode.GetString();
                if (!string.IsNullOrWhiteSpace(workspaceId))
                {
                    return workspaceId;
                }
            }
        }
        catch (DeployAIException ex) when (RailwayApiSupport.IsAuthorizationError(ex.Message))
        {
            // Fall back to scanning authorized workspaces.
        }

        var workspaceIds = await ListAuthorizedWorkspaceIdsAsync(credentials, cancellationToken);
        foreach (var workspaceId in workspaceIds)
        {
            if (await WorkspaceContainsProjectAsync(credentials, workspaceId, projectId, cancellationToken))
            {
                return workspaceId;
            }
        }

        return await ResolveWorkspaceIdAsync(credentials, cancellationToken);
    }

    private async Task<bool> WorkspaceContainsProjectAsync(
        ProviderCredentials credentials,
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken)
    {
        const string query = """
            query WorkspaceProject($workspaceId: String!) {
              workspace(workspaceId: $workspaceId) {
                projects {
                  edges {
                    node {
                      id
                    }
                  }
                }
              }
            }
            """;

        try
        {
            using var document = await RailwayApiSupport.ExecuteAsync(
                _httpClient,
                credentials.Token,
                query,
                new { workspaceId },
                cancellationToken);

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("workspace", out var workspaceNode) ||
                !workspaceNode.TryGetProperty("projects", out var projectsNode) ||
                !projectsNode.TryGetProperty("edges", out var projectEdges) ||
                projectEdges.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var projectEdge in projectEdges.EnumerateArray())
            {
                if (!projectEdge.TryGetProperty("node", out var projectNode))
                {
                    continue;
                }

                var id = projectNode.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
                if (string.Equals(id, projectId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (DeployAIException)
        {
            return false;
        }

        return false;
    }

    private async Task<IReadOnlyList<string>> ListAuthorizedWorkspaceIdsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        const string query = """
            query {
              me {
                workspaces {
                  id
                  name
                }
              }
            }
            """;

        try
        {
            using var document = await RailwayApiSupport.ExecuteAsync(
                _httpClient,
                credentials.Token,
                query,
                null,
                cancellationToken);

            var workspaceIds = ExtractWorkspaceIds(document.RootElement).ToList();
            if (workspaceIds.Count > 0)
            {
                return workspaceIds;
            }
        }
        catch (DeployAIException)
        {
            // Fall back to connection-shaped workspace query.
        }

        const string connectionQuery = """
            query {
              me {
                workspaces {
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

        using var connectionDocument = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            connectionQuery,
            null,
            cancellationToken);

        return ExtractWorkspaceIds(connectionDocument.RootElement).ToList();
    }

    private static IEnumerable<string> ExtractWorkspaceIds(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("me", out var me) ||
            !me.TryGetProperty("workspaces", out var workspacesNode))
        {
            yield break;
        }

        if (workspacesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var workspace in workspacesNode.EnumerateArray())
            {
                var workspaceId = workspace.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
                if (!string.IsNullOrWhiteSpace(workspaceId))
                {
                    yield return workspaceId;
                }
            }

            yield break;
        }

        if (!workspacesNode.TryGetProperty("edges", out var edges) ||
            edges.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var edge in edges.EnumerateArray())
        {
            if (!edge.TryGetProperty("node", out var node))
            {
                continue;
            }

            var workspaceId = node.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
            if (!string.IsNullOrWhiteSpace(workspaceId))
            {
                yield return workspaceId;
            }
        }
    }
}
