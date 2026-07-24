using DeployAI.Core.Deployments.Adapters;

namespace DeployAI.Core.Deployments.Graph.Transforms;

/// <summary>
/// Wires provisioned resources (databases, caches, buckets) into the services that consume
/// them, as ConnectsTo edges carrying the exact env keys the consumer's framework binds.
/// The key style comes from the consumer's adapter convention — emitting S3_* at a .NET app
/// (or Storage__* at a Node app) fails silently, with the app falling back to defaults.
/// </summary>
public static class ResourceWiringTransform
{
    public static DeploymentGraph Apply(DeploymentGraph graph, IFrameworkAdapterFactory adapters)
    {
        var consumers = graph.Services
            .Where(s => s.Capabilities.HasCapability(ServiceCapability.HttpService) ||
                        s.Capabilities.HasCapability(ServiceCapability.Worker))
            .ToList();

        foreach (var resource in graph.Services)
        {
            if (resource.Capabilities.HasCapability(ServiceCapability.Database))
            {
                foreach (var consumer in consumers)
                {
                    graph = graph
                        .WithEdge(new ConnectsToEdge(
                            consumer.Id,
                            resource.Id,
                            DatabaseEnvMapping(ConventionOf(consumer, adapters)),
                            ConnectionCredentialKind.ConnectionString,
                            // A missing database is fatal; the app cannot limp without it.
                            Required: true))
                        .WithEdge(new DependsOnEdge(consumer.Id, resource.Id, HealthGated: true));
                }
            }

            if (resource.Capabilities.HasCapability(ServiceCapability.ObjectStorage))
            {
                foreach (var consumer in consumers)
                {
                    graph = graph.WithEdge(new ConnectsToEdge(
                        consumer.Id,
                        resource.Id,
                        StorageEnvMapping(ConventionOf(consumer, adapters)),
                        ConnectionCredentialKind.S3Keys,
                        // Deliberately not fatal-by-default — but explicitly recorded, because
                        // the failure mode is worse than a crash: uploads silently land on a
                        // container filesystem that vanishes on the next redeploy.
                        Required: false));
                }
            }
        }

        return graph;
    }

    private static EnvKeyConvention ConventionOf(ServiceNode consumer, IFrameworkAdapterFactory adapters) =>
        (consumer.Framework is null ? null : adapters.GetById(consumer.Framework))?.EnvConvention
        ?? EnvKeyConvention.FlatUpperSnake;

    private static IReadOnlyDictionary<string, string> DatabaseEnvMapping(EnvKeyConvention convention) =>
        convention == EnvKeyConvention.DotnetSectionKey
            ? new Dictionary<string, string> { ["ConnectionStrings__Default"] = "connection-string" }
            : new Dictionary<string, string> { ["DATABASE_URL"] = "connection-string" };

    private static IReadOnlyDictionary<string, string> StorageEnvMapping(EnvKeyConvention convention) =>
        convention == EnvKeyConvention.DotnetSectionKey
            ? new Dictionary<string, string>
            {
                ["Storage__Endpoint"] = "endpoint",
                ["Storage__Region"] = "region",
                ["Storage__Bucket"] = "bucket",
                ["Storage__AccessKey"] = "access-key",
                ["Storage__SecretKey"] = "secret-key"
            }
            : new Dictionary<string, string>
            {
                ["S3_ENDPOINT"] = "endpoint",
                ["S3_REGION"] = "region",
                ["S3_BUCKET"] = "bucket",
                ["S3_ACCESS_KEY"] = "access-key",
                ["S3_SECRET_KEY"] = "secret-key"
            };
}
