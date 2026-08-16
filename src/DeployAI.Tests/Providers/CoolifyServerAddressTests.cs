using System.Net;
using DeployAI.Core.Providers;
using DeployAI.Providers.Coolify;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

/// <summary>
/// Reading the address a custom domain has to point at. Coolify has always returned the server's
/// <c>ip</c> and DeployAI has always dropped it, so the only address available was the one in the
/// instance URL — which is the control plane's, and happens to be the workload's only because both
/// run on one box today.
/// </summary>
public class CoolifyServerAddressTests
{
    private const string InstanceUrl = "https://46.225.80.188:8000";

    private static ProviderCredentials CredentialsFor(string instanceUrl) =>
        new(CoolifyCredentialStorage.Serialize(instanceUrl, "coolify-token"));

    private static MockedRequest RespondWithServers(MockHttpMessageHandler handler, string json) =>
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
            .Respond(HttpStatusCode.OK, "application/json", json);

    [Fact]
    public async Task TryGetServerAddressAsync_ReadsTheServersOwnIp()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithServers(handler, """[{ "uuid": "server-1", "name": "hetzner", "ip": "203.0.113.40" }]""");
        var provider = new CoolifyProvider(handler.ToHttpClient());

        var address = await provider.TryGetServerAddressAsync(
            CredentialsFor(InstanceUrl), serverUuid: null, CancellationToken.None);

        Assert.Equal("203.0.113.40", address);
    }

    [Fact]
    public async Task TryGetServerAddressAsync_PicksTheNamedServer_WhenThereAreSeveral()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithServers(handler, """
            [
              { "uuid": "server-1", "name": "one", "ip": "203.0.113.40" },
              { "uuid": "server-2", "name": "two", "ip": "203.0.113.41" }
            ]
            """);
        var provider = new CoolifyProvider(handler.ToHttpClient());

        var address = await provider.TryGetServerAddressAsync(
            CredentialsFor(InstanceUrl), "server-2", CancellationToken.None);

        Assert.Equal("203.0.113.41", address);
    }

    // Sending someone to point DNS at the wrong host is worse than telling them we cannot say which
    // host to use, so an ambiguous answer falls back to the instance address rather than guessing
    // between servers.
    [Fact]
    public async Task TryGetServerAddressAsync_DoesNotChooseBetweenServers_WhenNoneIsNamed()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithServers(handler, """
            [
              { "uuid": "server-1", "name": "one", "ip": "203.0.113.40" },
              { "uuid": "server-2", "name": "two", "ip": "203.0.113.41" }
            ]
            """);
        var provider = new CoolifyProvider(handler.ToHttpClient());

        var address = await provider.TryGetServerAddressAsync(
            CredentialsFor(InstanceUrl), serverUuid: null, CancellationToken.None);

        Assert.Equal("46.225.80.188", address);
    }

    // Coolify's own localhost server reports its "ip" as host.docker.internal, and a real instance
    // returned exactly that. Trusting the field verbatim told the user to point an A record at a
    // Docker hostname, and left the DNS check comparing resolved addresses against a string no
    // record could ever match — so a correctly configured domain would have waited out its deadline
    // and been reported as failed.
    [Theory]
    [InlineData("host.docker.internal")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.20")]
    [InlineData("172.17.0.1")]
    [InlineData("169.254.10.1")]
    [InlineData("::1")]
    [InlineData("")]
    public async Task TryGetServerAddressAsync_RejectsAnAddressNoOnesDnsCouldPointAt(string reported)
    {
        var handler = new MockHttpMessageHandler();
        RespondWithServers(
            handler, $$"""[{ "uuid": "server-1", "name": "localhost", "ip": "{{reported}}" }]""");
        var provider = new CoolifyProvider(handler.ToHttpClient());

        var address = await provider.TryGetServerAddressAsync(
            CredentialsFor(InstanceUrl), serverUuid: null, CancellationToken.None);

        Assert.Equal("46.225.80.188", address);
    }

    [Fact]
    public async Task TryGetServerAddressAsync_IsNull_WhenNeitherTheServerNorTheInstanceIsPublic()
    {
        const string localInstance = "http://localhost:8000";
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{localInstance}/api/v1/servers")
            .Respond(HttpStatusCode.OK, "application/json",
                """[{ "uuid": "server-1", "name": "localhost", "ip": "host.docker.internal" }]""");
        var provider = new CoolifyProvider(handler.ToHttpClient());

        var address = await provider.TryGetServerAddressAsync(
            CredentialsFor(localInstance), serverUuid: null, CancellationToken.None);

        Assert.Null(address);
    }

    [Fact]
    public async Task TryGetServerAddressAsync_FallsBackToTheInstanceAddress_WhenTheServerHasNoIp()
    {
        var handler = new MockHttpMessageHandler();
        RespondWithServers(handler, """[{ "uuid": "server-1", "name": "hetzner" }]""");
        var provider = new CoolifyProvider(handler.ToHttpClient());

        var address = await provider.TryGetServerAddressAsync(
            CredentialsFor(InstanceUrl), serverUuid: null, CancellationToken.None);

        Assert.Equal("46.225.80.188", address);
    }

    [Fact]
    public async Task TryGetServerAddressAsync_FallsBackToTheInstanceAddress_WhenTheServersApiFails()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/servers")
            .Respond(HttpStatusCode.Forbidden, "application/json", """{"message":"nope"}""");
        var provider = new CoolifyProvider(handler.ToHttpClient());

        var address = await provider.TryGetServerAddressAsync(
            CredentialsFor(InstanceUrl), serverUuid: null, CancellationToken.None);

        Assert.Equal("46.225.80.188", address);
    }

    // Null means "we do not know", and every caller has to treat it as a reason to stop rather
    // than a value to use.
    [Fact]
    public async Task TryGetServerAddressAsync_IsNull_WhenNeitherTheApiNorTheInstanceUrlCanSay()
    {
        const string hostnameInstance = "https://coolify.example.com";
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{hostnameInstance}/api/v1/servers")
            .Respond(HttpStatusCode.OK, "application/json", """[{ "uuid": "server-1", "name": "one" }]""");
        var provider = new CoolifyProvider(handler.ToHttpClient());

        var address = await provider.TryGetServerAddressAsync(
            CredentialsFor(hostnameInstance), serverUuid: null, CancellationToken.None);

        Assert.Null(address);
    }
}
