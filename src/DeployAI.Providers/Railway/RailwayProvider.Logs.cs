using System.Runtime.CompilerServices;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider
{
    private const int MaxIdlePollRounds = 90;
    private const int MaxWaitingForBuildPollRounds = 150;
    /// <summary>
    /// When Railway flips to FAILED with almost no build output (e.g. Metal builder dies right after
    /// scheduling), keep polling logs briefly so delayed error lines and event payloads can arrive.
    /// </summary>
    internal static int PostFailureLogGraceRounds { get; set; } = 5;
    private const int SparseLogThreshold = 5;
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Polls Railway's build and deployment logs every 2 seconds and yields new lines, stopping at
    /// a terminal deployment status, after ~5 minutes of no new output, or after ~3 minutes still
    /// waiting for the build to start.
    /// </summary>
    public async IAsyncEnumerable<string> StreamLogsAsync(
        ProviderCredentials credentials,
        string deploymentId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>();
        var idleRounds = 0;
        var waitingForBuildRounds = 0;

        var pollRound = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            pollRound++;
            var status = await FetchDeploymentStatusAsync(credentials, deploymentId, cancellationToken);
            var batch = new List<string>();
            var buildLines = await FetchBuildLogLinesAsync(credentials, deploymentId, seen, cancellationToken);
            var deployLines = await FetchDeploymentLogLinesAsync(credentials, deploymentId, seen, cancellationToken);
            batch.AddRange(buildLines);
            batch.AddRange(deployLines);


            foreach (var line in batch)
            {
                yield return line;
            }

            if (IsTerminalStatus(status))
            {
                var needsGrace = IsFailedStatus(status) && seen.Count < SparseLogThreshold;
                if (needsGrace)
                {
                    await foreach (var graceLine in DrainSparseFailureLogsAsync(
                                       credentials,
                                       deploymentId,
                                       seen,
                                       pollRound,
                                       cancellationToken))
                    {
                        yield return graceLine;
                    }
                }

                yield break;
            }

            if (IsWaitingForBuild(status))
            {
                waitingForBuildRounds++;
                idleRounds = 0;
                if (waitingForBuildRounds >= MaxWaitingForBuildPollRounds)
                {
                    yield break;
                }
            }
            else
            {
                waitingForBuildRounds = 0;
                idleRounds = batch.Count == 0 ? idleRounds + 1 : 0;
                if (idleRounds >= MaxIdlePollRounds)
                {
                    yield break;
                }
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async IAsyncEnumerable<string> DrainSparseFailureLogsAsync(
        ProviderCredentials credentials,
        string deploymentId,
        HashSet<string> seen,
        int parentPollRound,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        for (var graceRound = 1; graceRound <= PostFailureLogGraceRounds; graceRound++)
        {
            await Task.Delay(PollInterval, cancellationToken);
            var buildLines = await FetchBuildLogLinesAsync(credentials, deploymentId, seen, cancellationToken);
            var deployLines = await FetchDeploymentLogLinesAsync(credentials, deploymentId, seen, cancellationToken);


            foreach (var line in buildLines.Concat(deployLines))
            {
                yield return line;
            }

            if (seen.Count >= SparseLogThreshold)
            {
                break;
            }
        }

        foreach (var line in await FetchDeploymentEventErrorLinesAsync(credentials, deploymentId, seen, cancellationToken))
        {
            yield return line;
        }
    }

    private async Task<string?> FetchDeploymentStatusAsync(
        ProviderCredentials credentials,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.DeploymentStatus.ExecuteAsync(deploymentId, cancellationToken);
        var data = RailwayApiSupport.EnsureData(result);
        return RailwayGraphQlMapping.NormalizeDeploymentStatus(data.Deployment.Status);
    }

    private async Task<IReadOnlyList<string>> FetchBuildLogLinesAsync(
        ProviderCredentials credentials,
        string deploymentId,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.BuildLogs.ExecuteAsync(deploymentId, 500, cancellationToken);
        var graphqlError = result.Errors.Count > 0 ? result.Errors[0].Message : null;
        var buildNotReady = RailwayApiSupport.IsBuildNotReadyError(graphqlError);
        var data = RailwayApiSupport.TryGetData(result, RailwayApiSupport.IsBuildNotReadyError);


        if (data?.BuildLogs is null)
        {
            return [];
        }

        return RailwayGraphQlMapping.CollectLogLines(
            data.BuildLogs.Select(log => (log.Message, log.Timestamp)),
            seen);
    }

    private async Task<IReadOnlyList<string>> FetchDeploymentLogLinesAsync(
        ProviderCredentials credentials,
        string deploymentId,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.DeploymentLogs.ExecuteAsync(deploymentId, 500, cancellationToken);
        var data = RailwayApiSupport.TryGetData(result, static _ => false);
        if (data?.DeploymentLogs is null)
        {
            return [];
        }

        return RailwayGraphQlMapping.CollectLogLines(
            data.DeploymentLogs.Select(log => (log.Message, log.Timestamp)),
            seen);
    }

    private async Task<IReadOnlyList<string>> FetchDeploymentEventErrorLinesAsync(
        ProviderCredentials credentials,
        string deploymentId,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        await using var gql = _graphQl.CreateSession(credentials);
        var result = await gql.Client.DeploymentEvents.ExecuteAsync(deploymentId, 20, cancellationToken);
        var data = RailwayApiSupport.TryGetData(result, static _ => true);
        if (data?.DeploymentEvents?.Edges is null)
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var edge in data.DeploymentEvents.Edges)
        {
            var payload = edge.Node?.Payload;
            if (payload is null)
            {
                continue;
            }

            var detail = FirstNonBlank(payload.Error, payload.Reason, payload.Detail);
            if (string.IsNullOrWhiteSpace(detail))
            {
                continue;
            }

            var step = edge.Node!.Step.ToString();
            var message = $"Railway {step}: {detail}";
            if (!seen.Add(message))
            {
                continue;
            }

            lines.Add(message);
        }


        return lines;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool IsTerminalStatus(string? status) =>
        status is "SUCCESS" or "SUCCEEDED" or "ACTIVE" or "FAILED" or "CRASHED" or "REMOVED";

    private static bool IsFailedStatus(string? status) =>
        status is "FAILED" or "CRASHED" or "REMOVED";

    private static bool IsWaitingForBuild(string? status) =>
        status is "INITIALIZING" or "QUEUED" or "WAITING";
}
