using System.Text.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider : IDeploymentProvider, IProviderManagement
{
    private readonly HttpClient _httpClient;

    public RailwayProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string ProviderName => "railway";
    public string DisplayName => "Railway";
    public string ApiStyle => "graphql";

    public async Task<bool> ValidateCredentialsAsync(ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await RailwayApiSupport.ExecuteAsync(
                _httpClient,
                credentials.Token,
                "query { me { id } }",
                null,
                cancellationToken);
            return document.RootElement.GetProperty("data").TryGetProperty("me", out var me) &&
                   me.TryGetProperty("id", out _);
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

        const string mutation = """
            mutation Deploy($serviceId: String!, $environmentId: String!, $commitSha: String) {
              serviceInstanceDeployV2(serviceId: $serviceId, environmentId: $environmentId, commitSha: $commitSha)
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            mutation,
            new { serviceId, environmentId, commitSha = hasCommitSha ? commitSha : null },
            cancellationToken);

        var deploymentId = document.RootElement
            .GetProperty("data")
            .GetProperty("serviceInstanceDeployV2")
            .GetString();

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
        const string query = """
            query DeploymentStatus($id: String!) {
              deployment(id: $id) {
                id
                status
                url
              }
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { id = deploymentId },
            cancellationToken);

        var deployment = document.RootElement.GetProperty("data").GetProperty("deployment");
        var status = deployment.GetProperty("status").GetString()?.ToUpperInvariant();
        var url = deployment.TryGetProperty("url", out var urlNode) ? urlNode.GetString() : null;

        return status switch
        {
            "SUCCESS" or "SUCCEEDED" or "ACTIVE" => new DeploymentStatus(DeploymentStatusKind.Success, url, null),
            "FAILED" or "CRASHED" or "REMOVED" => new DeploymentStatus(
                DeploymentStatusKind.Failed,
                null,
                "Publishing did not go through on Railway."),
            "BUILDING" or "DEPLOYING" or "INITIALIZING" or "QUEUED" =>
                new DeploymentStatus(DeploymentStatusKind.InProgress, null, null),
            _ => new DeploymentStatus(DeploymentStatusKind.InProgress, null, null)
        };
    }

    private static string? GetPreferredEnvironmentId(JsonElement projectNode)
    {
        string? fallback = null;
        foreach (var (environmentId, environmentName) in EnumerateEnvironments(projectNode))
        {
            fallback ??= environmentId;
            if (string.Equals(environmentName, "production", StringComparison.OrdinalIgnoreCase))
            {
                return environmentId;
            }
        }

        return fallback;
    }

    private static IEnumerable<(string Id, string? Name)> EnumerateEnvironments(JsonElement projectNode)
    {
        if (!projectNode.TryGetProperty("environments", out var environmentsNode))
        {
            yield break;
        }

        if (environmentsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var environment in environmentsNode.EnumerateArray())
            {
                var environmentId = environment.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(environmentId))
                {
                    continue;
                }

                var environmentName = environment.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                yield return (environmentId, environmentName);
            }

            yield break;
        }

        if (!environmentsNode.TryGetProperty("edges", out var edges) ||
            edges.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var environmentEdge in edges.EnumerateArray())
        {
            if (!environmentEdge.TryGetProperty("node", out var environmentNode))
            {
                continue;
            }

            var environmentId = environmentNode.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(environmentId))
            {
                continue;
            }

            var environmentName = environmentNode.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            yield return (environmentId, environmentName);
        }
    }
}
