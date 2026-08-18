namespace DeployAI.Tests.Integration;

/// <summary>
/// Guards the wiring in Program.cs's "Frontend" CORS policy, not just the pure predicate
/// (<see cref="DeployAI.Tests.Services.DevCorsOriginPolicyTests"/> covers that). Runs in the
/// "Testing" environment — not "Development" — so it exercises the strict, single-origin branch:
/// only the exact configured `App:FrontendUrl` is allowed, confirming the policy is actually
/// wired up and that arbitrary localhost ports are only ever allowed in real local dev.
/// </summary>
public class CorsPolicyTests : IClassFixture<DeployAIWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CorsPolicyTests(DeployAIWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ConfiguredFrontendOrigin_IsAllowed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "http://localhost:4200");

        var response = await _client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Equal("http://localhost:4200", values!.Single());
    }

    [Fact]
    public async Task OtherLocalhostPort_IsNotAllowedOutsideDevelopment()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "http://localhost:4202");

        var response = await _client.SendAsync(request);

        Assert.False(response.Headers.TryGetValues("Access-Control-Allow-Origin", out _));
    }
}
