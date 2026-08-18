using System.Net;
using DeployAI.Core.Providers;
using DeployAI.Providers.Vercel;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

/// <summary>
/// Whether Vercel still has the project a deploy target points at.
/// </summary>
/// <remarks>
/// The rate-limit case is the one that earns this capability its keep on Vercel: a 429 arrives when
/// DeployAI asks too often, and reporting that as a deleted project would tell a user their site was
/// gone at exactly the moment nothing was wrong with it.
/// </remarks>
public class VercelApplicationExistenceTests
{
    private const string ProjectId = "prj_abc123";

    private static readonly ProviderCredentials Credentials = new("vercel-token");

    private static VercelProvider CreateProvider(MockHttpMessageHandler handler)
    {
        var client = handler.ToHttpClient();
        client.BaseAddress = new Uri("https://api.vercel.com/");
        return new VercelProvider(client);
    }

    private static Task<ProviderApplicationExistence> CheckAsync(HttpStatusCode status, string body = "{}")
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"https://api.vercel.com/v9/projects/{ProjectId}")
            .Respond(status, "application/json", body);

        return CreateProvider(handler)
            .CheckApplicationExistsAsync(Credentials, ProjectId, CancellationToken.None);
    }

    [Fact]
    public async Task NotFound_IsAbsent()
    {
        var result = await CheckAsync(HttpStatusCode.NotFound);

        Assert.Equal(ProviderApplicationPresence.Absent, result.Presence);
        Assert.Contains("no longer exists", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ok_IsPresent()
    {
        var result = await CheckAsync(HttpStatusCode.OK, """{"id":"prj_abc123","name":"site"}""");

        Assert.Equal(ProviderApplicationPresence.Present, result.Presence);
        Assert.False(result.IsInconclusive);
    }

    /// <summary>Being throttled is not evidence of anything about the project.</summary>
    [Fact]
    public async Task RateLimited_IsUnknown_NotAbsent()
    {
        var result = await CheckAsync(HttpStatusCode.TooManyRequests);

        Assert.Equal(ProviderApplicationPresence.Unknown, result.Presence);
        Assert.NotEqual(ProviderApplicationPresence.Absent, result.Presence);
    }

    [Fact]
    public async Task Unauthorized_IsUnknown()
    {
        var result = await CheckAsync(HttpStatusCode.Unauthorized);

        Assert.Equal(ProviderApplicationPresence.Unknown, result.Presence);
        Assert.Contains("connection", result.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
