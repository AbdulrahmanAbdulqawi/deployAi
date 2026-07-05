using System.Net;
using DeployAI.Api.Services;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Vercel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeployAI.Tests.Integration;

public class VercelOAuthCallbackTests : IClassFixture<VercelOAuthWebApplicationFactory>
{
    private readonly VercelOAuthWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public VercelOAuthCallbackTests(VercelOAuthWebApplicationFactory factory)
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

        var response = await _client.GetAsync($"/api/auth/vercel/callback?code=test-code&state={state}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("vercel=connected", response.Headers.Location?.ToString());

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<DeployAIDbContext>();
        var credential = await db.ProviderCredentials
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ProviderName == "vercel");

        Assert.NotNull(credential);
        Assert.True(credential!.IsValid);
        Assert.Equal("Default", credential.Label);
        Assert.NotEmpty(credential.TokenEncrypted);
    }

    [Fact]
    public async Task Callback_WithInvalidState_RedirectsWithError()
    {
        var response = await _client.GetAsync("/api/auth/vercel/callback?code=test-code&state=invalid");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("vercel=invalid_state", response.Headers.Location?.ToString());
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

public sealed class VercelOAuthWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"DeployAI_VercelOAuth_{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<DeployAIDbContext>));
            services.AddDbContext<DeployAIDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<IVercelOAuthService>();
            services.AddSingleton<IVercelOAuthService, FakeVercelOAuthService>();
        });
    }
}

internal sealed class FakeVercelOAuthService : IVercelOAuthService
{
    public string BuildAuthorizationUrl(string state) =>
        $"https://vercel.com/integrations/test/new?state={state}";

    public Task<string> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult("vercel-oauth-token");

    public Task<VercelUserProfile> GetUserProfileAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(new VercelUserProfile("user_1", "test-user", "test@example.com"));
}
