using System.Runtime.CompilerServices;
using System.Text.Json;
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
        const string query = """
            query DeploymentStatus($id: String!) {
              deployment(id: $id) {
                status
              }
            }
            """;

        using var document = await RailwayApiSupport.ExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { id = deploymentId },
            cancellationToken);

        return document.RootElement
            .GetProperty("data")
            .GetProperty("deployment")
            .GetProperty("status")
            .GetString()
            ?.ToUpperInvariant();
    }

    private async Task<IReadOnlyList<string>> FetchBuildLogLinesAsync(
        ProviderCredentials credentials,
        string deploymentId,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        const string query = """
            query BuildLogs($deploymentId: String!, $limit: Int) {
              buildLogs(deploymentId: $deploymentId, limit: $limit) {
                message
                timestamp
              }
            }
            """;

        using var document = await RailwayApiSupport.TryExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { deploymentId, limit = 500 },
            RailwayApiSupport.IsBuildNotReadyError,
            cancellationToken);

        if (document is null)
        {
            return [];
        }

        if (!document.RootElement.GetProperty("data").TryGetProperty("buildLogs", out var buildLogsNode))
        {
            return [];
        }

        return CollectLogLines(buildLogsNode, seen);
    }

    private async Task<IReadOnlyList<string>> FetchDeploymentLogLinesAsync(
        ProviderCredentials credentials,
        string deploymentId,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        const string query = """
            query DeploymentLogs($deploymentId: String!, $limit: Int) {
              deploymentLogs(deploymentId: $deploymentId, limit: $limit) {
                message
                timestamp
              }
            }
            """;

        using var document = await RailwayApiSupport.TryExecuteAsync(
            _httpClient,
            credentials.Token,
            query,
            new { deploymentId, limit = 500 },
            static _ => false,
            cancellationToken);

        if (document is null ||
            !document.RootElement.GetProperty("data").TryGetProperty("deploymentLogs", out var deploymentLogsNode))
        {
            return [];
        }

        return CollectLogLines(deploymentLogsNode, seen);
    }

    private static bool IsTerminalStatus(string? status) =>
        status is "SUCCESS" or "SUCCEEDED" or "ACTIVE" or "FAILED" or "CRASHED" or "REMOVED";

    private static bool IsWaitingForBuild(string? status) =>
        status is "INITIALIZING" or "QUEUED" or "WAITING";

    private static IReadOnlyList<string> CollectLogLines(JsonElement logsNode, HashSet<string> seen)
    {
        if (logsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var batch = new List<string>();
        foreach (var logEntry in logsNode.EnumerateArray())
        {
            var message = logEntry.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            var timestamp = logEntry.TryGetProperty("timestamp", out var timestampNode) ? timestampNode.GetString() : null;
            var key = string.IsNullOrWhiteSpace(timestamp) ? message : $"{timestamp}|{message}";
            if (!seen.Add(key))
            {
                continue;
            }

            batch.Add(message);
        }

        return batch;
    }
}
