using System.Net.Http.Headers;
using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;
using Microsoft.Extensions.DependencyInjection;

namespace DeployAI.Providers.Railway;

/// <summary>Creates a short-lived, per-call StrawberryShake GraphQL client session authenticated with a given credential's token.</summary>
public sealed class RailwayGraphQlClientFactory
{
    public const string GraphQlEndpoint = "https://backboard.railway.com/graphql/v2";

    internal IHttpClientFactory? TestHttpClientFactory { get; init; }

    /// <summary>Creates a new authenticated GraphQL session; dispose it after the call completes.</summary>
    public RailwayGraphQlSession CreateSession(ProviderCredentials credentials) =>
        new(credentials, TestHttpClientFactory);
}

/// <summary>A disposable scope wrapping one generated <see cref="IRailwayClient"/> instance and its DI container.</summary>
public sealed class RailwayGraphQlSession : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;

    public IRailwayClient Client { get; }

    internal RailwayGraphQlSession(ProviderCredentials credentials, IHttpClientFactory? testHttpClientFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(
            testHttpClientFactory ?? new RailwayGraphQlHttpClientFactory(credentials));
        services.AddRailwayClient();

        _serviceProvider = services.BuildServiceProvider();
        Client = _serviceProvider.GetRequiredService<IRailwayClient>();
    }

    public ValueTask DisposeAsync()
    {
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }

        _serviceProvider.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Builds the HttpClient the generated Railway GraphQL client sends requests through, with the bearer token attached.</summary>
internal sealed class RailwayGraphQlHttpClientFactory(ProviderCredentials credentials) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(new RailwayGraphQlResponseHandler { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(RailwayGraphQlClientFactory.GraphQlEndpoint)
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credentials.Token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeployAI");
        return client;
    }
}
