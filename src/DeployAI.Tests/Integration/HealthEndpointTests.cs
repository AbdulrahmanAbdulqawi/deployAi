using System.Net;
using DeployAI.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeployAI.Tests.Integration;

public class HealthEndpointTests : IClassFixture<DeployAIWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(DeployAIWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("DeployAI", payload);
    }

    [Fact]
    public async Task DatabaseHealth_ReturnsOk_WithTableCounts()
    {
        var response = await _client.GetAsync("/api/health/db");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", payload);
        Assert.Contains("migrationsApplied", payload);
        Assert.Contains("users", payload);
        Assert.Contains("projects", payload);
    }
}

public class DeployAIWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<DeployAIDbContext>));
            services.AddDbContext<DeployAIDbContext>(options =>
                options.UseInMemoryDatabase("DeployAI_Test"));
        });
    }
}
