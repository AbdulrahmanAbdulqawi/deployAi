using System.Net.Http.Headers;
using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;
using Microsoft.Extensions.DependencyInjection;

namespace DeployAI.Providers.Railway;

public sealed class RailwayGraphQlClientFactory
{
    public const string GraphQlEndpoint = "https://backboard.railway.com/graphql/v2";

    internal IHttpClientFactory? TestHttpClientFactory { get; init; }

    public RailwayGraphQlSession CreateSession(ProviderCredentials credentials) =>
        new(credentials, TestHttpClientFactory);
}

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
