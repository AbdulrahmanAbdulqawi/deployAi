using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Api.Services;

public interface IProviderHealthService
{
    Task<ProviderHealthSummary> GetSummaryAsync(CancellationToken cancellationToken);
}

public sealed record ProviderHealthSummary(IReadOnlyList<ProviderHealthStatus> Providers);

public sealed record ProviderHealthStatus(string Name, string Status, string? Message);

public sealed class ProviderHealthService : IProviderHealthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private static ProviderHealthSummary? _cache;
    private static DateTimeOffset _cacheExpiresAt;

    public ProviderHealthService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ProviderHealthSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
        {
            return _cache;
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        var vercel = await FetchVercelStatusAsync(client, cancellationToken);
        var railway = await FetchRailwayStatusAsync(client, cancellationToken);
        var coolify = FetchCoolifyStatus();

        _cache = new ProviderHealthSummary([vercel, railway, coolify]);
        _cacheExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        return _cache;
    }

    private static async Task<ProviderHealthStatus> FetchVercelStatusAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetFromJsonAsync<VercelStatusResponse>(
                "https://www.vercel-status.com/api/v2/status.json",
                cancellationToken);
            var indicator = response?.Status?.Indicator ?? "unknown";
            return new ProviderHealthStatus(
                "vercel",
                MapIndicator(indicator),
                response?.Status?.Description);
        }
        catch
        {
            return new ProviderHealthStatus("vercel", "unknown", "Could not reach the status page.");
        }
    }

    private static Task<ProviderHealthStatus> FetchRailwayStatusAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        // Railway does not expose a simple public status JSON endpoint in this integration.
        return Task.FromResult(new ProviderHealthStatus("railway", "unknown", null));
    }

    private static ProviderHealthStatus FetchCoolifyStatus() =>
        new("coolify", "self-hosted", "Coolify runs on your own server — check your instance directly.");

    private static string MapIndicator(string indicator) => indicator switch
    {
        "none" => "operational",
        "minor" => "degraded",
        "major" => "incident",
        "critical" => "incident",
        _ => "unknown"
    };

    private sealed class VercelStatusResponse
    {
        [JsonPropertyName("status")]
        public VercelStatusBody? Status { get; init; }
    }

    private sealed class VercelStatusBody
    {
        [JsonPropertyName("indicator")]
        public string? Indicator { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }
}
