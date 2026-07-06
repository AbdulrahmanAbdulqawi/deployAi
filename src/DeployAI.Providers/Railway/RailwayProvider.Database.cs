using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;

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

        var volumeChanged = await EnsurePostgresVolumeAsync(
            credentials,
            service.ProjectId,
            service.EnvironmentId,
            service.ServiceId,
            cancellationToken);

        var configChanged = await EnsurePostgresPluginVariablesAsync(
            credentials,
            service.ServiceId,
            service.EnvironmentId,
            postgresDatabaseName,
            cancellationToken);

        if (volumeChanged || configChanged)
        {
            await RedeployServiceInstanceAsync(credentials, service.ServiceId, service.EnvironmentId, cancellationToken);
        }

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

        var volumeChanged = await EnsureRedisVolumeAsync(
            credentials,
            service.ProjectId,
            service.EnvironmentId,
            service.ServiceId,
            cancellationToken);

        var configChanged = await EnsureRedisPluginVariablesAsync(
            credentials,
            service.ServiceId,
            service.EnvironmentId,
            cancellationToken);

        if (volumeChanged || configChanged)
        {
            await RedeployServiceInstanceAsync(credentials, service.ServiceId, service.EnvironmentId, cancellationToken);
        }

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

        (string Id, string SerializedConfig)? template = null;
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
        (string Id, string SerializedConfig)? template,
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
        try
        {
            await using var gql = _graphQl.CreateSession(credentials);
            var result = await gql.Client.CreateDatabaseService.ExecuteAsync(
                new ServiceCreateInput
                {
                    ProjectId = projectId,
                    EnvironmentId = environmentId,
                    Name = serviceName,
                    Source = new ServiceSourceInput { Image = dockerImage }
                },
                cancellationToken);
            var data = RailwayApiSupport.EnsureData(result);
            var serviceId = data.ServiceCreate.Id;
            var createdName = data.ServiceCreate.Name;
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

    private async Task<(string Id, string SerializedConfig)> GetTemplateAsync(
        ProviderCredentials credentials,
        string templateCode,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.Template.ExecuteAsync(templateCode, cancellationToken);
        var data = RailwayApiSupport.EnsureData(result);
        var id = data.Template.Id;
        var serializedConfig = data.Template.SerializedConfig;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(serializedConfig))
        {
            throw new DeployAIException("railway_api_error", $"Railway did not return a {templateCode} template.");
        }

        return (id, serializedConfig);
    }

    private async Task DeployTemplateAsync(
        ProviderCredentials credentials,
        (string Id, string SerializedConfig) template,
        string projectId,
        string environmentId,
        string? workspaceId,
        CancellationToken cancellationToken)
    {
        var input = new TemplateDeployV2Input
        {
            TemplateId = template.Id,
            SerializedConfig = template.SerializedConfig,
            ProjectId = projectId,
            EnvironmentId = environmentId
        };

        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            input = input with { WorkspaceId = workspaceId };
        }

        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.TemplateDeploy.ExecuteAsync(input, cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
    }

    private async Task<(string ServiceId, string ServiceName)?> FindDatabaseServiceAsync(
        ProviderCredentials credentials,
        string projectId,
        string environmentId,
        IReadOnlyList<string> imageMatchers,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.ProjectServices.ExecuteAsync(projectId, cancellationToken);
        var data = RailwayApiSupport.TryGetData(result, static _ => false);
        if (data?.Project?.Services?.Edges is null)
        {
            return null;
        }

        foreach (var serviceEdge in data.Project.Services.Edges)
        {
            var serviceNode = serviceEdge.Node;
            if (serviceNode is null ||
                string.IsNullOrWhiteSpace(serviceNode.Id) ||
                string.IsNullOrWhiteSpace(serviceNode.Name))
            {
                continue;
            }

            if (serviceNode.ServiceInstances?.Edges is null)
            {
                continue;
            }

            foreach (var instanceEdge in serviceNode.ServiceInstances.Edges)
            {
                var instanceNode = instanceEdge.Node;
                if (instanceNode is null ||
                    !string.Equals(instanceNode.EnvironmentId, environmentId, StringComparison.Ordinal))
                {
                    continue;
                }

                var image = instanceNode.Source?.Image;
                if (MatchesDatabaseImage(image, imageMatchers))
                {
                    return (serviceNode.Id, serviceNode.Name);
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
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.ProjectServicesByName.ExecuteAsync(projectId, cancellationToken);
        var data = RailwayApiSupport.TryGetData(result, static _ => false);
        if (data?.Project?.Services?.Edges is null)
        {
            return null;
        }

        foreach (var serviceEdge in data.Project.Services.Edges)
        {
            var serviceNode = serviceEdge.Node;
            if (serviceNode is null ||
                string.IsNullOrWhiteSpace(serviceNode.Id) ||
                string.IsNullOrWhiteSpace(serviceNode.Name) ||
                !string.Equals(serviceNode.Name, serviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (serviceNode.ServiceInstances?.Edges is null)
            {
                continue;
            }

            foreach (var instanceEdge in serviceNode.ServiceInstances.Edges)
            {
                var instanceNode = instanceEdge.Node;
                if (instanceNode is null)
                {
                    continue;
                }

                if (string.Equals(instanceNode.EnvironmentId, environmentId, StringComparison.Ordinal))
                {
                    return (serviceNode.Id, serviceNode.Name);
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
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.UpdateDatabaseServiceImage.ExecuteAsync(
            serviceId,
            environmentId,
            new ServiceInstanceUpdateInput
            {
                Source = new ServiceSourceInput { Image = dockerImage }
            },
            cancellationToken);
        RailwayApiSupport.EnsureSuccess(result);
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
