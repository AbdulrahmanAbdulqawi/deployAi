using System.Text.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider : IProviderDatabaseProvisioning
{
    private static readonly string[] PostgresImageMatchers =
    [
        "postgres-ssl",
        "railwayapp-templates/postgres",
        "library/postgres",
        "postgres:16",
        "postgres:17",
        "postgres:15"
    ];

    private static readonly string[] RedisImageMatchers =
    [
        "library/redis",
        "redis:7",
        "redis:6",
        "redis:alpine"
    ];

    public async Task<ProvisionedDatabaseService?> EnsurePostgresAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        string? postgresDatabaseName,
        CancellationToken cancellationToken)
    {
        var service = await EnsureTemplateDatabaseAsync(
            credentials,
            appProviderProjectId,
            templateCode: "postgres",
            imageMatchers: PostgresImageMatchers,
            cancellationToken);
        if (service is null)
        {
            return null;
        }

        await EnsurePostgresVolumeAsync(
            credentials,
            service.ProjectId,
            service.EnvironmentId,
            service.ServiceId,
            cancellationToken);

        await EnsurePostgresPluginVariablesAsync(
            credentials,
            service.ServiceId,
            service.EnvironmentId,
            postgresDatabaseName,
            cancellationToken);

        return service;
    }

    public async Task<ProvisionedDatabaseService?> EnsureRedisAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        CancellationToken cancellationToken)
    {
        var service = await EnsureTemplateDatabaseAsync(
            credentials,
            appProviderProjectId,
            templateCode: "redis",
            imageMatchers: RedisImageMatchers,
            cancellationToken);
        if (service is null)
        {
            return null;
        }

        await EnsureRedisVolumeAsync(
            credentials,
            service.ProjectId,
            service.EnvironmentId,
            service.ServiceId,
            cancellationToken);

        await EnsureRedisPluginVariablesAsync(
            credentials,
            service.ServiceId,
            service.EnvironmentId,
            cancellationToken);

        return service;
    }

    public async Task LinkDatabaseVariablesAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        IReadOnlyList<DatabaseVariableLink> links,
        CancellationToken cancellationToken)
    {
        if (links.Count == 0)
        {
            return;
        }

        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(appProviderProjectId);
        foreach (var link in links)
        {
            await EnsureServiceVariableAsync(
                credentials,
                serviceId,
                environmentId,
                link.Key,
                link.ReferenceValue,
                cancellationToken);
        }
    }

    private async Task<ProvisionedDatabaseService?> EnsureTemplateDatabaseAsync(
        ProviderCredentials credentials,
        string appProviderProjectId,
        string templateCode,
        IReadOnlyList<string> imageMatchers,
        CancellationToken cancellationToken)
    {
        var (appServiceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(appProviderProjectId);
        var (projectId, workspaceIdHint) = await GetServiceContextAsync(credentials, appServiceId, cancellationToken);
        var serviceName = DefaultServiceName(templateCode);
        var dockerImage = DefaultDockerImage(templateCode);
        var existing = await FindDatabaseServiceAsync(
            credentials,
            projectId,
            environmentId,
            imageMatchers,
            cancellationToken);
        if (existing is not null)
        {
            return new ProvisionedDatabaseService(
                existing.Value.ServiceId,
                existing.Value.ServiceName,
                projectId,
                environmentId);
        }

        var existingByName = await FindDatabaseServiceByNameAsync(
            credentials,
            projectId,
            environmentId,
            serviceName,
            cancellationToken);
        if (existingByName is not null)
        {
            await UpdateDatabaseServiceImageAsync(
                credentials,
                existingByName.Value.ServiceId,
                environmentId,
                dockerImage,
                cancellationToken);

            return new ProvisionedDatabaseService(
                existingByName.Value.ServiceId,
                existingByName.Value.ServiceName,
                projectId,
                environmentId);
        }

        var workspaceId = await ResolveWorkspaceIdForProjectAsync(
            credentials,
            projectId,
            workspaceIdHint,
            cancellationToken);

        (string Id, JsonElement SerializedConfig)? template = null;
        try
        {
            template = await GetTemplateAsync(credentials, templateCode, cancellationToken);
        }
        catch (DeployAIException ex) when (RailwayApiSupport.IsAuthorizationError(ex.Message))
        {
            // Fall back to direct service creation below.
        }

        var (serviceId, provisionedServiceName) = await ProvisionDatabaseServiceAsync(
            credentials,
            templateCode,
            template,
            projectId,
            environmentId,
            workspaceId,
            serviceName,
            dockerImage,
            cancellationToken);

        return new ProvisionedDatabaseService(
            serviceId,
            provisionedServiceName,
            projectId,
            environmentId);
    }

    private static string DefaultServiceName(string templateCode) =>
        string.Equals(templateCode, "redis", StringComparison.OrdinalIgnoreCase) ? "Redis" : "Postgres";

    private static string DefaultDockerImage(string templateCode) =>
        string.Equals(templateCode, "redis", StringComparison.OrdinalIgnoreCase)
            ? "redis:7-alpine"
            : "ghcr.io/railwayapp-templates/postgres-ssl:16";

    private async Task<(string ServiceId, string ServiceName)> ProvisionDatabaseServiceAsync(
        ProviderCredentials credentials,
        string templateCode,
        (string Id, JsonElement SerializedConfig)? template,
        string projectId,
        string environmentId,
        string workspaceId,
        string serviceName,
        string dockerImage,
        CancellationToken cancellationToken)
    {
        if (template is not null)
        {
            try
            {
                await DeployTemplateAsync(
                    credentials,
                    template.Value,
                    projectId,
                    environmentId,
                    workspaceId,
                    cancellationToken);
            }
            catch (DeployAIException ex) when (RailwayApiSupport.IsAuthorizationError(ex.Message))
            {
                try
                {
                    await DeployTemplateAsync(
                        credentials,
                        template.Value,
                        projectId,
                        environmentId,
                        workspaceId: null,
                        cancellationToken);
                }
                catch (DeployAIException templateEx) when (RailwayApiSupport.IsAuthorizationError(templateEx.Message))
                {
                    return await CreateDatabaseServiceViaImageAsync(
                        credentials,
                        projectId,
                        environmentId,
                        serviceName,
                        dockerImage,
                        cancellationToken);
                }
            }

            var created = await WaitForDatabaseServiceAsync(
                credentials,
                projectId,
                environmentId,
                templateCode,
                cancellationToken);
            if (created is not null)
            {
                return created.Value;
            }

            return await CreateDatabaseServiceViaImageAsync(
                credentials,
                projectId,
                environmentId,
                serviceName,
                dockerImage,
                cancellationToken);
        }

        return await CreateDatabaseServiceViaImageAsync(
            credentials,
            projectId,
            environmentId,
            serviceName,
            dockerImage,
            cancellationToken);
    }

    private async Task<(string ServiceId, string ServiceName)?> WaitForDatabaseServiceAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string templateCode,
        CancellationToken cancellationToken)
    {
        var imageMatchers = string.Equals(templateCode, "redis", StringComparison.OrdinalIgnoreCase)
            ? RedisImageMatchers
            : PostgresImageMatchers;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var existing = await FindDatabaseServiceAsync(
                credentials,
                projectId,
                environmentId,
                imageMatchers,
                cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return null;
    }

    private async Task<(string ServiceId, string ServiceName)> CreateDatabaseServiceViaImageAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string serviceName,
        string dockerImage,
        CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation CreateDatabaseService($input: ServiceCreateInput!) {
              serviceCreate(input: $input) {
                id
                name
              }
            }
            """;

        try
        {
            using var document = await RailwayApiSupport.ExecuteAsync(
                _httpClient,
                credentials.Token,
                mutation,
                new
                {
                    input = new
                    {
                        projectId,
                        environmentId,
                        name = serviceName,
                        source = new { image = dockerImage }
                    }
                },
                cancellationToken);

            var serviceNode = document.RootElement.GetProperty("data").GetProperty("serviceCreate");
            var serviceId = serviceNode.GetProperty("id").GetString();
            var createdName = serviceNode.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(serviceId) || string.IsNullOrWhiteSpace(createdName))
            {
                throw new DeployAIException(
                    "railway_database_provision_failed",
                    "Railway did not return the new database service.");
            }

            return (serviceId, createdName);
        }
        catch (DeployAIException ex) when (RailwayApiSupport.IsDuplicateServiceNameError(ex.Message))
        {
            var existing = await FindDatabaseServiceByNameAsync(
                credentials,
                projectId,
                environmentId,
                serviceName,
                cancellationToken);
            if (existing is null)
            {
                throw;
            }

            await UpdateDatabaseServiceImageAsync(
                credentials,
                existing.Value.ServiceId,
                environmentId,
                dockerImage,
                cancellationToken);

            return existing.Value;
        }
        catch (DeployAIException ex) when (RailwayApiSupport.IsAuthorizationError(ex.Message))
        {
            throw new DeployAIException(
                "railway_database_not_authorized",
                "Railway blocked creating PostgreSQL/Redis for this workspace. In Connections, open Railway → Advanced and paste an account token from https://railway.com/account/tokens (choose “No workspace” when creating it). Your Railway role must be Admin or Deployer.");
        }
    }

    private async Task<(string Id, JsonElement SerializedConfig)> GetTemplateAsync(
        ProviderCredentials credentials,
        string templateCode,
        CancellationToken cancellationToken)
    {
        const string query = """
            query Template($code: String!) {
              template(code: $code) {
                id
                serializedConfig
              }
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { code = templateCode },
            cancellationToken);

        var template = document.RootElement.GetProperty("data").GetProperty("template");
        var id = template.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new DeployAIException("railway_api_error", $"Railway did not return a {templateCode} template.");
        }

        return (id, template.GetProperty("serializedConfig").Clone());
    }

    private async Task DeployTemplateAsync(
        ProviderCredentials credentials,
        (string Id, JsonElement SerializedConfig) template,
        string projectId,
        string environmentId,
        string? workspaceId,
        CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation DeployTemplate($input: TemplateDeployV2Input!) {
              templateDeployV2(input: $input) {
                projectId
                workflowId
              }
            }
            """;

        var serializedConfig = template.SerializedConfig.ValueKind == JsonValueKind.String
            ? template.SerializedConfig.GetString()!
            : template.SerializedConfig.GetRawText();

        object input = string.IsNullOrWhiteSpace(workspaceId)
            ? new
            {
                templateId = template.Id,
                serializedConfig,
                projectId,
                environmentId
            }
            : new
            {
                templateId = template.Id,
                serializedConfig,
                projectId,
                environmentId,
                workspaceId
            };

        await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            mutation,
            new { input },
            cancellationToken);
    }

    private async Task<(string ServiceId, string ServiceName)?> FindDatabaseServiceAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        IReadOnlyList<string> imageMatchers,
        CancellationToken cancellationToken)
    {
        const string query = """
            query ProjectServices($id: String!) {
              project(id: $id) {
                services {
                  edges {
                    node {
                      id
                      name
                      serviceInstances {
                        edges {
                          node {
                            environmentId
                            source {
                              image
                            }
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
            new { id = projectId },
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("project", out var projectNode) ||
            !projectNode.TryGetProperty("services", out var servicesNode) ||
            !servicesNode.TryGetProperty("edges", out var serviceEdges) ||
            serviceEdges.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var serviceEdge in serviceEdges.EnumerateArray())
        {
            if (!serviceEdge.TryGetProperty("node", out var serviceNode))
            {
                continue;
            }

            var serviceId = serviceNode.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
            var serviceName = serviceNode.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(serviceId) || string.IsNullOrWhiteSpace(serviceName))
            {
                continue;
            }

            if (!serviceNode.TryGetProperty("serviceInstances", out var instancesNode) ||
                !instancesNode.TryGetProperty("edges", out var instanceEdges) ||
                instanceEdges.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var instanceEdge in instanceEdges.EnumerateArray())
            {
                if (!instanceEdge.TryGetProperty("node", out var instanceNode))
                {
                    continue;
                }

                var instanceEnvironmentId = instanceNode.TryGetProperty("environmentId", out var envNode)
                    ? envNode.GetString()
                    : null;
                if (!string.Equals(instanceEnvironmentId, environmentId, StringComparison.Ordinal))
                {
                    continue;
                }

                var image = instanceNode.TryGetProperty("source", out var sourceNode) &&
                            sourceNode.TryGetProperty("image", out var imageNode)
                    ? imageNode.GetString()
                    : null;
                if (MatchesDatabaseImage(image, imageMatchers))
                {
                    return (serviceId, serviceName);
                }
            }
        }

        return null;
    }

    private async Task<(string ServiceId, string ServiceName)?> FindDatabaseServiceByNameAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        string serviceName,
        CancellationToken cancellationToken)
    {
        const string query = """
            query ProjectServices($id: String!) {
              project(id: $id) {
                services {
                  edges {
                    node {
                      id
                      name
                      serviceInstances {
                        edges {
                          node {
                            environmentId
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
            new { id = projectId },
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("project", out var projectNode) ||
            !projectNode.TryGetProperty("services", out var servicesNode) ||
            !servicesNode.TryGetProperty("edges", out var serviceEdges) ||
            serviceEdges.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var serviceEdge in serviceEdges.EnumerateArray())
        {
            if (!serviceEdge.TryGetProperty("node", out var serviceNode))
            {
                continue;
            }

            var serviceId = serviceNode.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
            var matchedName = serviceNode.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(serviceId) ||
                string.IsNullOrWhiteSpace(matchedName) ||
                !string.Equals(matchedName, serviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!serviceNode.TryGetProperty("serviceInstances", out var instancesNode) ||
                !instancesNode.TryGetProperty("edges", out var instanceEdges) ||
                instanceEdges.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var instanceEdge in instanceEdges.EnumerateArray())
            {
                if (!instanceEdge.TryGetProperty("node", out var instanceNode))
                {
                    continue;
                }

                var instanceEnvironmentId = instanceNode.TryGetProperty("environmentId", out var envNode)
                    ? envNode.GetString()
                    : null;
                if (string.Equals(instanceEnvironmentId, environmentId, StringComparison.Ordinal))
                {
                    return (serviceId, matchedName);
                }
            }
        }

        return null;
    }

    private async Task UpdateDatabaseServiceImageAsync(
        ProviderCredentials credentials,
        string serviceId,
        string environmentId,
        string dockerImage,
        CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation UpdateDatabaseServiceImage($serviceId: String!, $environmentId: String!, $input: ServiceInstanceUpdateInput!) {
              serviceInstanceUpdate(serviceId: $serviceId, environmentId: $environmentId, input: $input)
            }
            """;

        await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            mutation,
            new
            {
                serviceId,
                environmentId,
                input = new
                {
                    source = new { image = dockerImage }
                }
            },
            cancellationToken);
    }

    private static bool MatchesDatabaseImage(string? image, IReadOnlyList<string> imageMatchers)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return false;
        }

        if (image.Contains("ghcr.io/railway/postgres", StringComparison.OrdinalIgnoreCase) ||
            image.Contains("ghcr.io/railway/redis", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var matcher in imageMatchers)
        {
            if (image.Contains(matcher, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
