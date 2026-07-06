using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

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

    public async Task<DeploymentResponse> TriggerDeploymentAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string branch,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);

        await ApplyBuildConfigurationAsync(credentials, serviceId, environmentId, environment, cancellationToken);

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
        return RailwayGraphQlMapping.MapDeploymentStatus(data.Deployment.Status, data.Deployment.Url);
    }
}
