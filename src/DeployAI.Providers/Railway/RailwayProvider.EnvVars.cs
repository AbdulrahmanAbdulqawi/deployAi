using System.Text.Json;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider
{
    public async Task<IReadOnlyList<ProviderEnvVar>> ListEnvVarsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);
        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);

        const string query = """
            query Variables($projectId: String!, $environmentId: String!, $serviceId: String!) {
              variables(projectId: $projectId, environmentId: $environmentId, serviceId: $serviceId)
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { projectId, environmentId, serviceId },
            cancellationToken);

        var variablesNode = document.RootElement.GetProperty("data").GetProperty("variables");
        if (variablesNode.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var envVars = new List<ProviderEnvVar>();
        foreach (var property in variablesNode.EnumerateObject())
        {
            var value = property.Value.GetString();
            var isSecret = RailwayApiSupport.LooksLikeSecret(property.Name, value);
            envVars.Add(new ProviderEnvVar(
                property.Name,
                property.Name,
                isSecret ? null : value,
                "plain",
                [],
                isSecret));
        }

        return envVars.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ProviderEnvVar> UpsertEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        UpsertProviderEnvVarRequest request,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);
        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);

        const string mutation = """
            mutation VariableUpsert($input: VariableUpsertInput!) {
              variableUpsert(input: $input)
            }
            """;

        await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            mutation,
            new
            {
                input = new
                {
                    projectId,
                    environmentId,
                    serviceId,
                    name = request.Key,
                    value = request.Value,
                    skipDeploys = true
                }
            },
            cancellationToken);

        return new ProviderEnvVar(
            request.Key,
            request.Key,
            null,
            request.Type,
            request.Targets.ToList(),
            true);
    }

    public async Task DeleteEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string envVarId,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);
        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);

        const string mutation = """
            mutation VariableDelete($input: VariableDeleteInput!) {
              variableDelete(input: $input)
            }
            """;

        await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            mutation,
            new
            {
                input = new
                {
                    projectId,
                    environmentId,
                    serviceId,
                    name = envVarId
                }
            },
            cancellationToken);
    }

    private async Task<string> GetProjectIdForServiceAsync(
        ProviderCredentials credentials,
        string serviceId,
        CancellationToken cancellationToken)
    {
        const string query = """
            query ServiceProject($id: String!) {
              service(id: $id) {
                projectId
              }
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { id = serviceId },
            cancellationToken);

        var projectId = document.RootElement
            .GetProperty("data")
            .GetProperty("service")
            .GetProperty("projectId")
            .GetString();

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new DeployAI.Core.Exceptions.DeployAIException(
                "railway_api_error",
                "Could not resolve the Railway project for this service.");
        }

        return projectId;
    }

    private async Task<(string ProjectId, string? WorkspaceId)> GetServiceContextAsync(
        ProviderCredentials credentials,
        string serviceId,
        CancellationToken cancellationToken)
    {
        const string query = """
            query ServiceContext($id: String!) {
              service(id: $id) {
                projectId
                project {
                  workspaceId
                }
              }
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { id = serviceId },
            cancellationToken);

        var serviceNode = document.RootElement.GetProperty("data").GetProperty("service");
        var projectId = serviceNode.GetProperty("projectId").GetString();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new DeployAI.Core.Exceptions.DeployAIException(
                "railway_api_error",
                "Could not resolve the Railway project for this service.");
        }

        string? workspaceId = null;
        if (serviceNode.TryGetProperty("project", out var projectNode) &&
            projectNode.TryGetProperty("workspaceId", out var workspaceNode))
        {
            workspaceId = workspaceNode.GetString();
        }

        return (projectId, workspaceId);
    }
}
