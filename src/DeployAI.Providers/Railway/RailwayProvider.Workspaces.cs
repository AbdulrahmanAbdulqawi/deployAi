using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;

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

        try
        {
            await using var gql = _graphQl.CreateSession(credentials);
            var result = await gql.Client.ProjectWorkspace.ExecuteAsync(projectId, cancellationToken);
            var data = RailwayApiSupport.TryGetData(result, static _ => false);
            var workspaceId = data?.Project.WorkspaceId;
            if (!string.IsNullOrWhiteSpace(workspaceId))
            {
                return workspaceId;
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
        try
        {
            await using var gql = _graphQl.CreateSession(credentials);
            var result = await gql.Client.WorkspaceProjectIds.ExecuteAsync(workspaceId, cancellationToken);
            var data = RailwayApiSupport.TryGetData(result, static _ => false);
            if (data?.Workspace?.Projects?.Edges is null)
            {
                return false;
            }

            foreach (var projectEdge in data.Workspace.Projects.Edges)
            {
                if (string.Equals(projectEdge.Node?.Id, projectId, StringComparison.Ordinal))
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
        try
        {
            await using var gql = _graphQl.CreateSession(credentials);
            var result = await gql.Client.ListWorkspaces.ExecuteAsync(cancellationToken);
            var data = RailwayApiSupport.TryGetData(result, static _ => false);
            var workspaceIds = RailwayGraphQlMapping.ExtractWorkspaceIds(data).ToList();
            if (workspaceIds.Count > 0)
            {
                return workspaceIds;
            }
        }
        catch (DeployAIException)
        {
            // No alternate query shape in the committed schema.
        }

        return [];
    }
}
