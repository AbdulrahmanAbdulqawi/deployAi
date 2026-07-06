using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;

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

        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.GetVariables.ExecuteAsync(projectId, environmentId, serviceId, cancellationToken);
        var data = RailwayApiSupport.EnsureData(result);
        var variables = RailwayGraphQlMapping.ParseVariablesJson(data.Variables);

        var envVars = new List<ProviderEnvVar>();
        foreach (var (name, value) in variables)
        {
            var isSecret = RailwayApiSupport.LooksLikeSecret(name, value);
            envVars.Add(new ProviderEnvVar(
                name,
                name,
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

        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.VariableUpsert.ExecuteAsync(
            new VariableUpsertInput
            {
                ProjectId = projectId,
                EnvironmentId = environmentId,
                ServiceId = serviceId,
                Name = request.Key,
                Value = request.Value,
                SkipDeploys = true
            },
            cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);

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

        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.VariableDelete.ExecuteAsync(
            new VariableDeleteInput
            {
                ProjectId = projectId,
                EnvironmentId = environmentId,
                ServiceId = serviceId,
                Name = envVarId
            },
            cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
    }

    private async Task<string> GetProjectIdForServiceAsync(
        ProviderCredentials credentials,
        string serviceId,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.ServiceProject.ExecuteAsync(serviceId, cancellationToken);
        var data = RailwayApiSupport.EnsureData(result);
        var projectId = data.Service.ProjectId;

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
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.ServiceContext.ExecuteAsync(serviceId, cancellationToken);
        var data = RailwayApiSupport.EnsureData(result);
        var projectId = data.Service.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new DeployAI.Core.Exceptions.DeployAIException(
                "railway_api_error",
                "Could not resolve the Railway project for this service.");
        }

        return (projectId, data.Service.Project?.WorkspaceId);
    }
}
