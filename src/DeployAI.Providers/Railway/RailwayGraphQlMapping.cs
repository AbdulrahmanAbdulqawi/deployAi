using System.Text.Json;
using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;
using GraphQlDeploymentStatus = DeployAI.Providers.Railway.GraphQL.DeploymentStatus;

namespace DeployAI.Providers.Railway;

internal static class RailwayGraphQlMapping
{
    internal static string NormalizeDeploymentStatus(GraphQlDeploymentStatus status) =>
        status.ToString().ToUpperInvariant();

    internal static Core.Providers.DeploymentStatus MapDeploymentStatus(GraphQlDeploymentStatus status, string? url) =>
        status switch
        {
            GraphQlDeploymentStatus.Success => new Core.Providers.DeploymentStatus(DeploymentStatusKind.Success, url, null),
            GraphQlDeploymentStatus.Failed or GraphQlDeploymentStatus.Crashed or GraphQlDeploymentStatus.Removed =>
                new Core.Providers.DeploymentStatus(DeploymentStatusKind.Failed, null, "Publishing did not go through on Railway."),
            GraphQlDeploymentStatus.Building or GraphQlDeploymentStatus.Deploying or GraphQlDeploymentStatus.Initializing or GraphQlDeploymentStatus.Queued =>
                new Core.Providers.DeploymentStatus(DeploymentStatusKind.InProgress, null, null),
            _ => new Core.Providers.DeploymentStatus(DeploymentStatusKind.InProgress, null, null)
        };

    internal static string MapServiceDeploymentStatus(GraphQlDeploymentStatus status) =>
        status switch
        {
            GraphQlDeploymentStatus.Success => "running",
            GraphQlDeploymentStatus.Failed or GraphQlDeploymentStatus.Crashed or GraphQlDeploymentStatus.Removed => "failed",
            GraphQlDeploymentStatus.Building or GraphQlDeploymentStatus.Deploying or GraphQlDeploymentStatus.Initializing or GraphQlDeploymentStatus.Queued => "deploying",
            _ => status.ToString().ToLowerInvariant()
        };
    internal static string? GetPreferredEnvironmentId(IEnumerable<(string Id, string? Name)> environments)
    {
        string? fallback = null;
        foreach (var (environmentId, environmentName) in environments)
        {
            fallback ??= environmentId;
            if (string.Equals(environmentName, "production", StringComparison.OrdinalIgnoreCase))
            {
                return environmentId;
            }
        }

        return fallback;
    }

    internal static IEnumerable<(string Id, string? Name)> EnumerateEnvironmentEdges<TEdge>(
        IReadOnlyList<TEdge>? edges,
        Func<TEdge, (string Id, string? Name)?> selector)
    {
        if (edges is null)
        {
            yield break;
        }

        foreach (var edge in edges)
        {
            var environment = selector(edge);
            if (environment is not null)
            {
                yield return environment.Value;
            }
        }
    }

    internal static void AppendListProjects(
        List<ProviderProject> projects,
        IListProjectsResult? data)
    {
        if (data?.Projects?.Edges is null)
        {
            return;
        }

        foreach (var projectEdge in data.Projects.Edges)
        {
            AppendProjectNode(projects, projectEdge.Node);
        }
    }

    internal static void AppendWorkspaceProjects(
        List<ProviderProject> projects,
        IWorkspaceProjectsResult? data)
    {
        if (data?.Workspace?.Projects?.Edges is null)
        {
            return;
        }

        foreach (var projectEdge in data.Workspace.Projects.Edges)
        {
            AppendWorkspaceProjectNode(projects, projectEdge.Node);
        }
    }

    internal static void AppendExternalWorkspaces(
        List<ProviderProject> projects,
        IListExternalWorkspacesResult? data)
    {
        if (data?.ExternalWorkspaces is null)
        {
            return;
        }

        foreach (var workspace in data.ExternalWorkspaces)
        {
            foreach (var project in workspace.Projects)
            {
                AppendExternalProjectNode(projects, project);
            }
        }
    }

    internal static ServiceInstanceUpdateInput ToServiceInstanceUpdateInput(Dictionary<string, object?> settings)
    {
        var hasRoot = settings.TryGetValue("rootDirectory", out var rootDirectory);
        var hasBuild = settings.TryGetValue("buildCommand", out var buildCommand);
        var hasStart = settings.TryGetValue("startCommand", out var startCommand);

        if (hasRoot && hasBuild && hasStart)
        {
            return new ServiceInstanceUpdateInput
            {
                RootDirectory = rootDirectory?.ToString(),
                BuildCommand = buildCommand?.ToString(),
                StartCommand = startCommand?.ToString()
            };
        }

        if (hasRoot && hasBuild)
        {
            return new ServiceInstanceUpdateInput
            {
                RootDirectory = rootDirectory?.ToString(),
                BuildCommand = buildCommand?.ToString()
            };
        }

        if (hasRoot && hasStart)
        {
            return new ServiceInstanceUpdateInput
            {
                RootDirectory = rootDirectory?.ToString(),
                StartCommand = startCommand?.ToString()
            };
        }

        if (hasRoot)
        {
            return new ServiceInstanceUpdateInput { RootDirectory = rootDirectory?.ToString() };
        }

        if (hasBuild && hasStart)
        {
            return new ServiceInstanceUpdateInput
            {
                BuildCommand = buildCommand?.ToString(),
                StartCommand = startCommand?.ToString()
            };
        }

        if (hasBuild)
        {
            return new ServiceInstanceUpdateInput { BuildCommand = buildCommand?.ToString() };
        }

        if (hasStart)
        {
            return new ServiceInstanceUpdateInput { StartCommand = startCommand?.ToString() };
        }

        return new ServiceInstanceUpdateInput();
    }

    internal static IReadOnlyDictionary<string, string?> ParseVariablesJson(string? variablesJson)
    {
        if (string.IsNullOrWhiteSpace(variablesJson))
        {
            return new Dictionary<string, string?>();
        }

        using var document = JsonDocument.Parse(variablesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string?>();
        }

        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            variables[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText()
            };
        }

        return variables;
    }

    internal static IReadOnlyList<string> CollectLogLines(
        IEnumerable<(string? Message, string? Timestamp)> entries,
        HashSet<string> seen)
    {
        var batch = new List<string>();
        foreach (var (message, timestamp) in entries)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            var key = string.IsNullOrWhiteSpace(timestamp) ? message : $"{timestamp}|{message}";
            if (!seen.Add(key))
            {
                continue;
            }

            batch.Add(message);
        }

        return batch;
    }

    internal static IEnumerable<string> ExtractWorkspaceIds(IListWorkspacesResult? data)
    {
        if (data?.Me?.Workspaces is null)
        {
            yield break;
        }

        foreach (var workspace in data.Me.Workspaces)
        {
            if (!string.IsNullOrWhiteSpace(workspace.Id))
            {
                yield return workspace.Id;
            }
        }
    }

    private static void AppendExternalProjectNode(
        List<ProviderProject> projects,
        IListExternalWorkspaces_ExternalWorkspaces_Projects projectNode)
    {
        var projectName = projectNode.Name ?? "Railway project";
        var environmentId = GetPreferredEnvironmentId(
            EnumerateEnvironmentEdges(
                projectNode.Environments?.Edges,
                edge => edge.Node is null
                    ? null
                    : (edge.Node.Id, edge.Node.Name)));
        if (environmentId is null)
        {
            return;
        }

        if (projectNode.Services?.Edges is null)
        {
            return;
        }

        foreach (var serviceEdge in projectNode.Services.Edges)
        {
            AddServiceProject(projects, projectName, environmentId, serviceEdge.Node?.Id, serviceEdge.Node?.Name);
        }
    }

    private static void AppendProjectNode(List<ProviderProject> projects, IListProjects_Projects_Edges_Node? projectNode)
    {
        if (projectNode is null)
        {
            return;
        }

        var projectName = projectNode.Name ?? "Railway project";
        var environmentId = GetPreferredEnvironmentId(
            EnumerateEnvironmentEdges(
                projectNode.Environments?.Edges,
                edge => edge.Node is null
                    ? null
                    : (edge.Node.Id, edge.Node.Name)));
        if (environmentId is null || projectNode.Services?.Edges is null)
        {
            return;
        }

        foreach (var serviceEdge in projectNode.Services.Edges)
        {
            AddServiceProject(projects, projectName, environmentId, serviceEdge.Node?.Id, serviceEdge.Node?.Name);
        }
    }

    private static void AppendWorkspaceProjectNode(
        List<ProviderProject> projects,
        IWorkspaceProjects_Workspace_Projects_Edges_Node? projectNode)
    {
        if (projectNode is null)
        {
            return;
        }

        var projectName = projectNode.Name ?? "Railway project";
        var environmentId = GetPreferredEnvironmentId(
            EnumerateEnvironmentEdges(
                projectNode.Environments?.Edges,
                edge => edge.Node is null
                    ? null
                    : (edge.Node.Id, edge.Node.Name)));
        if (environmentId is null || projectNode.Services?.Edges is null)
        {
            return;
        }

        foreach (var serviceEdge in projectNode.Services.Edges)
        {
            AddServiceProject(projects, projectName, environmentId, serviceEdge.Node?.Id, serviceEdge.Node?.Name);
        }
    }

    private static void AddServiceProject(
        List<ProviderProject> projects,
        string projectName,
        string environmentId,
        string? serviceId,
        string? serviceName)
    {
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
