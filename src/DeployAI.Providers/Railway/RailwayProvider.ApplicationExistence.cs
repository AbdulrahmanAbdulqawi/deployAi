using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider : IProviderApplicationExistence
{
    /// <summary>
    /// Asks Railway whether a service still exists, keeping "deleted" apart from "we could not look".
    /// </summary>
    /// <remarks>
    /// Deliberately does not go through <c>RailwayApiSupport.TryGetData</c>. That helper throws on
    /// any GraphQL error, and <see cref="GetServiceStatusAsync"/> then reports both a missing service
    /// and an unreadable response as the same <c>"unknown"</c> — which is exactly the conflation this
    /// capability exists to undo. Here the two signals are read separately: a <c>null</c> service in
    /// an otherwise clean response is Railway saying it is gone, while any error at all means the
    /// question was not answered.
    /// </remarks>
    public async Task<ProviderApplicationExistence> CheckApplicationExistsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerProjectId))
        {
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Unknown, null, null,
                "This app has no Railway service recorded against it yet.");
        }

        var (serviceId, environmentId) = RailwayApiSupport.ParseProviderProjectId(providerProjectId);

        try
        {
            await using var gql = _graphQl.CreateSession(credentials);
            var result = await gql.Client.ServiceStatus.ExecuteAsync(serviceId, cancellationToken);

            if (result.Errors.Count > 0)
            {
                return new ProviderApplicationExistence(
                    ProviderApplicationPresence.Unknown, null, null,
                    "Railway returned an error when asked about this app, so DeployAI could not check it.");
            }

            if (result.Data is null)
            {
                return new ProviderApplicationExistence(
                    ProviderApplicationPresence.Unknown, null, null,
                    "Railway returned an empty response when asked about this app.");
            }

            if (result.Data.Service is null)
            {
                return new ProviderApplicationExistence(
                    ProviderApplicationPresence.Absent, null, null,
                    "The service this app deploys to no longer exists on Railway.");
            }

            var edges = result.Data.Service.ServiceInstances?.Edges;
            if (edges is null)
            {
                // The service is there; only its per-environment detail is missing. Existence is
                // already answered, and answering it is what this check is for.
                return new ProviderApplicationExistence(
                    ProviderApplicationPresence.Present, null, null,
                    "The service exists on Railway.");
            }

            foreach (var edge in edges)
            {
                var node = edge.Node;
                if (node is null ||
                    !string.Equals(node.EnvironmentId, environmentId, StringComparison.Ordinal))
                {
                    continue;
                }

                var deployment = node.LatestDeployment;
                if (deployment is null)
                {
                    return new ProviderApplicationExistence(
                        ProviderApplicationPresence.Present, "not_deployed", null,
                        "The service exists on Railway but has never been deployed.");
                }

                var state = RailwayGraphQlMapping.MapServiceDeploymentStatus(deployment.Status);
                return new ProviderApplicationExistence(
                    ProviderApplicationPresence.Present, state, deployment.Url,
                    $"The service exists on Railway and reports \"{state}\".");
            }

            // The service exists but not in the environment this target points at. That is a real
            // finding — the target is aimed at something that is not there — but it is not the same
            // as the service having been deleted, so it is reported as its own state.
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Absent, null, null,
                "The service exists on Railway but not in the environment this app deploys to.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProviderApplicationExistence(
                ProviderApplicationPresence.Unknown, null, null,
                $"DeployAI could not reach Railway to check this app ({ex.GetType().Name}).");
        }
    }
}
