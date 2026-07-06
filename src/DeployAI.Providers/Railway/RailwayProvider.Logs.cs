using System.Runtime.CompilerServices;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Railway;

public sealed partial class RailwayProvider
{
    private const int MaxIdlePollRounds = 90;
    private const int MaxWaitingForBuildPollRounds = 150;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public async IAsyncEnumerable<string> StreamLogsAsync(
        ProviderCredentials credentials,
        string deploymentId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>();
        var idleRounds = 0;
        var waitingForBuildRounds = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var status = await FetchDeploymentStatusAsync(credentials, deploymentId, cancellationToken);
            var batch = new List<string>();
            batch.AddRange(await FetchBuildLogLinesAsync(credentials, deploymentId, seen, cancellationToken));
            batch.AddRange(await FetchDeploymentLogLinesAsync(credentials, deploymentId, seen, cancellationToken));

            foreach (var line in batch)
            {
                yield return line;
            }

            if (IsTerminalStatus(status))
            {
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

    private static bool IsTerminalStatus(string? status) =>
        status is "SUCCESS" or "SUCCEEDED" or "ACTIVE" or "FAILED" or "CRASHED" or "REMOVED";

    private static bool IsWaitingForBuild(string? status) =>
        status is "INITIALIZING" or "QUEUED" or "WAITING";
}
