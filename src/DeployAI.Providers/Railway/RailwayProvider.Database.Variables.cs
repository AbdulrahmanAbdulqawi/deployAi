using System.Security.Cryptography;

using System.Text.Json;

using DeployAI.Core.Providers;



namespace DeployAI.Providers.Railway;



public sealed partial class RailwayProvider

{

    private async Task EnsurePostgresPluginVariablesAsync(

        ProviderCredentials credentials,

        string serviceId,

        string environmentId,

        string? postgresDatabaseName,

        CancellationToken cancellationToken)

    {

        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);

        var databaseName = NormalizePostgresDatabaseName(postgresDatabaseName);

        var hasPassword = await ServiceHasVariableAsync(

            credentials,

            projectId,

            environmentId,

            serviceId,

            "POSTGRES_PASSWORD",

            cancellationToken);

        var hasDatabaseUrl = await ServiceHasVariableAsync(

            credentials,

            projectId,

            environmentId,

            serviceId,

            "DATABASE_URL",

            cancellationToken);



        if (hasPassword && hasDatabaseUrl)

        {

            var currentDbName = await GetServiceVariableValueAsync(

                credentials,

                projectId,

                environmentId,

                serviceId,

                "POSTGRES_DB",

                cancellationToken);

            if (!string.Equals(currentDbName, databaseName, StringComparison.Ordinal))

            {

                await EnsureServiceVariableAsync(

                    credentials,

                    serviceId,

                    environmentId,

                    "POSTGRES_DB",

                    databaseName,

                    cancellationToken);

                await RedeployServiceInstanceAsync(credentials, serviceId, environmentId, cancellationToken);

            }



            await EnsurePostgresDataDirectoryAsync(credentials, serviceId, environmentId, cancellationToken);

            return;

        }



        var password = GenerateSecretToken();

        await EnsureServiceVariableAsync(credentials, serviceId, environmentId, "POSTGRES_USER", "postgres", cancellationToken);

        await EnsureServiceVariableAsync(credentials, serviceId, environmentId, "POSTGRES_PASSWORD", password, cancellationToken);

        await EnsureServiceVariableAsync(credentials, serviceId, environmentId, "POSTGRES_DB", databaseName, cancellationToken);

        await EnsureServiceVariableAsync(

            credentials,

            serviceId,

            environmentId,

            "DATABASE_URL",

            "postgresql://${{POSTGRES_USER}}:${{POSTGRES_PASSWORD}}@${{RAILWAY_PRIVATE_DOMAIN}}:5432/${{POSTGRES_DB}}",

            cancellationToken);



        await EnsurePostgresDataDirectoryAsync(credentials, serviceId, environmentId, cancellationToken);

        await RedeployServiceInstanceAsync(credentials, serviceId, environmentId, cancellationToken);

    }



    private async Task EnsurePostgresDataDirectoryAsync(

        ProviderCredentials credentials,

        string serviceId,

        string environmentId,

        CancellationToken cancellationToken)

    {

        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);

        const string pgDataPath = "/var/lib/postgresql/data/pgdata";

        var currentPgData = await GetServiceVariableValueAsync(

            credentials,

            projectId,

            environmentId,

            serviceId,

            "PGDATA",

            cancellationToken);

        if (string.Equals(currentPgData, pgDataPath, StringComparison.Ordinal))

        {

            return;

        }

        await EnsureServiceVariableAsync(

            credentials,

            serviceId,

            environmentId,

            "PGDATA",

            pgDataPath,

            cancellationToken);

        await RedeployServiceInstanceAsync(credentials, serviceId, environmentId, cancellationToken);

    }



    private async Task EnsureRedisPluginVariablesAsync(

        ProviderCredentials credentials,

        string serviceId,

        string environmentId,

        CancellationToken cancellationToken)

    {

        var projectId = await GetProjectIdForServiceAsync(credentials, serviceId, cancellationToken);

        if (await ServiceHasVariableAsync(credentials, projectId, environmentId, serviceId, "REDIS_URL", cancellationToken))

        {

            return;

        }



        await EnsureServiceVariableAsync(

            credentials,

            serviceId,

            environmentId,

            "REDIS_URL",

            "redis://${{RAILWAY_PRIVATE_DOMAIN}}:6379",

            cancellationToken);



        await RedeployServiceInstanceAsync(credentials, serviceId, environmentId, cancellationToken);

    }



    private static string NormalizePostgresDatabaseName(string? name) =>

        string.IsNullOrWhiteSpace(name) ? "railway" : name.Trim();



    private async Task<bool> ServiceHasVariableAsync(

        ProviderCredentials credentials,

        string projectId,

        string environmentId,

        string serviceId,

        string name,

        CancellationToken cancellationToken)

    {

        var value = await GetServiceVariableValueAsync(

            credentials,

            projectId,

            environmentId,

            serviceId,

            name,

            cancellationToken);

        return !string.IsNullOrWhiteSpace(value);

    }



    private async Task<string?> GetServiceVariableValueAsync(

        ProviderCredentials credentials,

        string projectId,

        string environmentId,

        string serviceId,

        string name,

        CancellationToken cancellationToken)

    {

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



        if (!document.RootElement.TryGetProperty("data", out var data) ||

            !data.TryGetProperty("variables", out var variablesNode) ||

            variablesNode.ValueKind != JsonValueKind.Object ||

            !variablesNode.TryGetProperty(name, out var valueNode))

        {

            return null;

        }



        return valueNode.ValueKind switch

        {

            JsonValueKind.String => valueNode.GetString(),

            JsonValueKind.Null => null,

            _ => valueNode.ToString()

        };

    }



    private async Task RedeployServiceInstanceAsync(

        ProviderCredentials credentials,

        string serviceId,

        string environmentId,

        CancellationToken cancellationToken)

    {

        const string mutation = """

            mutation RedeployService($serviceId: String!, $environmentId: String!) {

              serviceInstanceDeployV2(serviceId: $serviceId, environmentId: $environmentId)

            }

            """;



        await RailwayApiSupport.ExecuteAsync(

            _httpClient,

            credentials.Token,

            mutation,

            new { serviceId, environmentId },

            cancellationToken);

    }



    private static string GenerateSecretToken() =>

        Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

}


