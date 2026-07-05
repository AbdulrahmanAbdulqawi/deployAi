using System.Net;
using DeployAI.Api.Services;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Railway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeployAI.Tests.Integration;

public class RailwayOAuthCallbackTests : IClassFixture<RailwayOAuthWebApplicationFactory>
{
    private readonly RailwayOAuthWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RailwayOAuthCallbackTests(RailwayOAuthWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Callback_StoresEncryptedCredential_AndRedirectsToFrontend()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        string state;
        using (var scope = _factory.Services.CreateScope())
        {
            var stateStore = scope.ServiceProvider.GetRequiredService<IOAuthStateStore>();
            state = stateStore.CreateState(new OAuthStatePayload(userId, "/settings"));
        }

        var response = await _client.GetAsync($"/api/auth/railway/callback?code=test-code&state={state}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("railway=connected", response.Headers.Location?.ToString());

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<DeployAIDbContext>();
        var credential = await db.ProviderCredentials
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ProviderName == "railway");

        Assert.NotNull(credential);
        Assert.True(credential!.IsValid);
        Assert.Equal("Test User", credential.Label);
        Assert.NotEmpty(credential.TokenEncrypted);
        Assert.True(RailwayOAuthTokenStorage.IsOAuthPayload(
            verifyScope.ServiceProvider.GetRequiredService<DeployAI.Core.Security.IEncryptionService>()
                .Decrypt(credential.TokenEncrypted)));
    }

    [Fact]
    public async Task Callback_WithInvalidState_RedirectsWithError()
    {
        var response = await _client.GetAsync("/api/auth/railway/callback?code=test-code&state=invalid");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("railway=invalid_state", response.Headers.Location?.ToString());
    }

    private async Task SeedUserAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeployAIDbContext>();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 12345,
            GitHubLogin = "test-user",
            GitHubTokenEncrypted = [1, 2, 3],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}

public sealed class RailwayOAuthWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"DeployAI_RailwayOAuth_{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<DeployAIDbContext>));
            services.AddDbContext<DeployAIDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<IRailwayOAuthService>();
            services.AddSingleton<IRailwayOAuthService, FakeRailwayOAuthService>();
        });
    }
}

internal sealed class FakeRailwayOAuthService : IRailwayOAuthService
{
    public string BuildAuthorizationUrl(string state) =>
        $"https://backboard.railway.com/oauth/auth?state={state}";

    public Task<RailwayOAuthTokens> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(new RailwayOAuthTokens("railway-oauth-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1)));

    public Task<RailwayOAuthTokens> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult(new RailwayOAuthTokens("railway-refreshed-token", refreshToken, DateTimeOffset.UtcNow.AddHours(1)));

    public Task<RailwayUserProfile> GetUserProfileAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(new RailwayUserProfile("user_1", "test@example.com", "Test User"));
}
