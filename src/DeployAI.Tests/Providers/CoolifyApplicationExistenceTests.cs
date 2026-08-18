using System.Net;
using DeployAI.Core.Providers;
using DeployAI.Providers.Coolify;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

/// <summary>
/// Whether Coolify still has the application a deploy target points at.
/// </summary>
/// <remarks>
/// Every test here exists to hold one line apart from another: a 404 means the application was
/// deleted, and anything else that is not a 2xx means DeployAI could not find out. The capability
/// this covers was written because <c>GetServiceStatusAsync</c> answers <c>"unknown"</c> to both —
/// <c>TryGetApplicationAsync</c> returns null for every non-2xx — so a deleted app and an
/// unreachable Coolify were byte-identical, and the dashboard kept advertising dead links.
/// </remarks>
public class CoolifyApplicationExistenceTests
{
    private const string InstanceUrl = "https://coolify.example.com";
    private const string ApplicationUuid = "app-uuid-1";

    private static readonly ProviderCredentials Credentials =
        new(CoolifyCredentialStorage.Serialize(InstanceUrl, "coolify-token"));

    private static CoolifyProvider CreateProvider(MockHttpMessageHandler handler) =>
        new(handler.ToHttpClient());

    private static MockHttpMessageHandler RespondWith(HttpStatusCode status, string body = "")
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/{ApplicationUuid}")
            .Respond(status, "application/json", body);
        return handler;
    }

    private static Task<ProviderApplicationExistence> CheckAsync(MockHttpMessageHandler handler) =>
        CreateProvider(handler).CheckApplicationExistsAsync(
            Credentials, ApplicationUuid, CancellationToken.None);

    /// <summary>The one case that means the application is genuinely gone.</summary>
    [Fact]
    public async Task NotFound_IsAbsent()
    {
        var result = await CheckAsync(RespondWith(HttpStatusCode.NotFound, """{"message":"Not found."}"""));

        Assert.Equal(ProviderApplicationPresence.Absent, result.Presence);
        Assert.False(result.IsInconclusive);
        Assert.Contains("no longer exists", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A revoked token says nothing about the application, and must not read as deleted.</summary>
    [Fact]
    public async Task Unauthorized_IsUnknown_AndNamesTheConnection()
    {
        var result = await CheckAsync(RespondWith(HttpStatusCode.Unauthorized));

        Assert.Equal(ProviderApplicationPresence.Unknown, result.Presence);
        Assert.True(result.IsInconclusive);
        Assert.Contains("connection", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerError_IsUnknown_NotAbsent()
    {
        var result = await CheckAsync(RespondWith(HttpStatusCode.InternalServerError));

        Assert.Equal(ProviderApplicationPresence.Unknown, result.Presence);
        Assert.NotEqual(ProviderApplicationPresence.Absent, result.Presence);
        Assert.Contains("500", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Coolify instance that is down answers nothing at all. Reporting that as a deleted app would
    /// tell every user their apps had been deleted the moment the host went unreachable.
    /// </summary>
    [Fact]
    public async Task ATransportFailure_IsUnknown_NotAbsent()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Get, $"{InstanceUrl}/api/v1/applications/{ApplicationUuid}")
            .Throw(new HttpRequestException("No such host is known."));

        var result = await CheckAsync(handler);

        Assert.Equal(ProviderApplicationPresence.Unknown, result.Presence);
        Assert.Contains("HttpRequestException", result.Detail, StringComparison.Ordinal);
        // The exception type, not its text: a raw transport error is not a user-facing sentence.
        Assert.DoesNotContain("No such host", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>Present, carrying Coolify's own word for what it is doing rather than a normalised one.</summary>
    [Fact]
    public async Task Ok_IsPresent_AndCarriesTheRawState()
    {
        var handler = RespondWith(HttpStatusCode.OK, """
            {
              "uuid": "app-uuid-1",
              "name": "mirqab-api",
              "status": "exited:unhealthy",
              "fqdn": "https://api.example.com"
            }
            """);

        var result = await CheckAsync(handler);

        Assert.Equal(ProviderApplicationPresence.Present, result.Presence);
        Assert.Equal("exited:unhealthy", result.State);
        Assert.Equal("https://api.example.com", result.DeployUrl);
    }

    /// <summary>
    /// A 200 is already proof the application exists, so an unexpected body shape must not downgrade
    /// the answer to "could not check".
    /// </summary>
    [Fact]
    public async Task Ok_WithAnUnreadableBody_IsStillPresent()
    {
        var result = await CheckAsync(RespondWith(HttpStatusCode.OK, "not json at all"));

        Assert.Equal(ProviderApplicationPresence.Present, result.Presence);
        Assert.Null(result.State);
    }

    /// <summary>A target with no application recorded has nothing to ask about.</summary>
    [Fact]
    public async Task NoApplicationRecorded_IsUnknown()
    {
        var result = await CreateProvider(new MockHttpMessageHandler())
            .CheckApplicationExistsAsync(Credentials, "", CancellationToken.None);

        Assert.Equal(ProviderApplicationPresence.Unknown, result.Presence);
    }
}
