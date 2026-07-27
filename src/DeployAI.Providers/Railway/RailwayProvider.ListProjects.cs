using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

/// <summary>Fallback project-listing paths for OAuth-scoped tokens, which can't use the account-level projects query <see cref="RailwayProvider.TryListRootProjectsAsync"/> uses.</summary>
public sealed partial class RailwayProvider
{
    /// <summary>Lists projects across every workspace the token is authorized for, falling back to external/shared workspaces if none of the user's own workspaces have any.</summary>
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
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.WorkspaceProjects.ExecuteAsync(workspaceId, cancellationToken);
        var data = RailwayApiSupport.EnsureData(result);
        var projects = new List<ProviderProject>();
        RailwayGraphQlMapping.AppendWorkspaceProjects(projects, data);
        return projects;
    }

    private async Task<IReadOnlyList<ProviderProject>> ListProjectsViaExternalWorkspacesAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.ListExternalWorkspaces.ExecuteAsync(cancellationToken);
        var data = RailwayApiSupport.EnsureData(result);
        var projects = new List<ProviderProject>();
        RailwayGraphQlMapping.AppendExternalWorkspaces(projects, data);
        return projects;
    }

    /// <summary>Lists projects via the account-level query, which only works for API tokens (not OAuth-scoped tokens).</summary>
    private async Task<IReadOnlyList<ProviderProject>> TryListRootProjectsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.ListProjects.ExecuteAsync(cancellationToken);
        var data = RailwayApiSupport.EnsureData(result);
        var projects = new List<ProviderProject>();
        RailwayGraphQlMapping.AppendListProjects(projects, data);
        return projects;
    }
}
