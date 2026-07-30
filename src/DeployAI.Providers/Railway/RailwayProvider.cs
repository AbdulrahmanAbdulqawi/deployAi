using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

/// <summary>
/// Railway GraphQL API client. Split into partial classes by concern: this file has the core
/// <see cref="IDeploymentProvider"/> deploy/status operations; <c>RailwayProvider.Management</c> has
/// project creation; <c>RailwayProvider.EnvVars</c> has environment variables;
/// <c>RailwayProvider.Database</c>/<c>.Database.Variables</c> have Postgres/Redis provisioning;
/// <c>RailwayProvider.DataServiceInspection</c> has database inspection;
/// <c>RailwayProvider.ServiceOperations</c> has status/redeploy/rollback/delete;
/// <c>RailwayProvider.Networking</c> has public domain assignment; <c>RailwayProvider.Settings</c>
/// has build config; <c>RailwayProvider.Volumes</c> has volume mounts;
/// <c>RailwayProvider.Workspaces</c>/<c>.ListProjects</c> have project/workspace listing; and
/// <c>RailwayProvider.Logs</c> has deployment log streaming.
/// </summary>
public sealed partial class RailwayProvider : IDeploymentProvider, IProviderManagement
{
    private readonly RailwayGraphQlClientFactory _graphQl;

    public RailwayProvider(RailwayGraphQlClientFactory graphQl)
    {
        _graphQl = graphQl;
    }

    public string ProviderName => "railway";
    public string DisplayName => "Railway";
    public string ApiStyle => "graphql";

    public async Task<bool> ValidateCredentialsAsync(ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            await using var gql = _graphQl.CreateSession(credentials);
            var result = await gql.Client.Me.ExecuteAsync(cancellationToken);
            var data = RailwayApiSupport.EnsureData(result);
            return !string.IsNullOrWhiteSpace(data.Me.Id);
        }
        catch (DeployAIException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lists projects via the account-level query first (works for API tokens), falling back to the
    /// OAuth-scoped workspace query if that's unauthorized or returns nothing (OAuth tokens can't
    /// use the account-level query at all).
    /// </summary>
    public async Task<IReadOnlyList<ProviderProject>> ListProjectsAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            var accountProjects = await TryListRootProjectsAsync(credentials, cancellationToken);
            if (accountProjects.Count > 0)
            {
                return accountProjects;
            }
        }
        catch (DeployAIException)
        {
            // OAuth tokens cannot use the account-level projects query.
        }

        return await ListProjectsViaOAuthAsync(credentials, cancellationToken);
    }

    /// <summary>
    /// Triggers a deployment, first pushing build configuration and ensuring a public domain
    /// exists for the service (both as side effects), then deploying at the given commit if one
    /// was supplied in <paramref name="environment"/>'s "commitSha" entry.
    /// </summary>
    public async Task<DeploymentResponse> TriggerDeploymentAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string branch,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);

        await ApplyBuildConfigurationAsync(credentials, serviceId, environmentId, environment, cancellationToken);
        await EnsurePublicServiceDomainAsync(credentials, serviceId, environmentId, environment, cancellationToken);

        environment.TryGetValue("commitSha", out var commitSha);
        var hasCommitSha = !string.IsNullOrWhiteSpace(commitSha);

        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.DeployService.ExecuteAsync(
            serviceId,
            environmentId,
            hasCommitSha ? commitSha : null,
            cancellationToken);
        var data = RailwayApiSupport.EnsureData(result);
        var deploymentId = data.ServiceInstanceDeployV2;

        if (string.IsNullOrWhiteSpace(deploymentId))
        {
            throw new InvalidOperationException("Railway returned an empty deployment id.");
        }

        return new DeploymentResponse(deploymentId, null);
    }

    public async Task<DeploymentStatus> GetStatusAsync(
        ProviderCredentials credentials,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.DeploymentStatus.ExecuteAsync(deploymentId, cancellationToken);
        var data = RailwayApiSupport.EnsureData(result);
        var failureDetail = RailwayGraphQlMapping.ExtractFailureDetail(
            data.Deployment.Diagnosis,
            data.Deployment.Meta);

        if (string.IsNullOrWhiteSpace(failureDetail) &&
            data.Deployment.Status is DeployAI.Providers.Railway.GraphQL.DeploymentStatus.Failed
                or DeployAI.Providers.Railway.GraphQL.DeploymentStatus.Crashed
                or DeployAI.Providers.Railway.GraphQL.DeploymentStatus.Removed)
        {
            failureDetail = await FetchDeploymentEventFailureDetailAsync(
                credentials,
                deploymentId,
                cancellationToken);
        }

        var mapped = RailwayGraphQlMapping.MapDeploymentStatus(
            data.Deployment.Status,
            data.Deployment.Url,
            failureDetail);


        return mapped;
    }

    private async Task<string?> FetchDeploymentEventFailureDetailAsync(
        ProviderCredentials credentials,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.DeploymentEvents.ExecuteAsync(deploymentId, 20, cancellationToken);
        var data = RailwayApiSupport.TryGetData(result, static _ => true);
        if (data?.DeploymentEvents?.Edges is null)
        {
            return null;
        }

        foreach (var edge in data.DeploymentEvents.Edges)
        {
            var payload = edge.Node?.Payload;
            if (payload is null)
            {
                continue;
            }

            var detail = FirstNonBlank(payload.Error, payload.Reason, payload.Detail);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                return $"Railway {edge.Node!.Step}: {detail}";
            }
        }

        return null;
    }
}
