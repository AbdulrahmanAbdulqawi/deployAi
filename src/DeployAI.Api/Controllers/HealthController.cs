using DeployAI.Api.Services;
using DeployAI.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeployAI.Api.Controllers;

/// <summary>
/// Liveness/readiness checks for DeployAI itself, its configured hosting providers, and its
/// database - not authenticated, since these are used by uptime monitors/load balancers.
/// </summary>
[ApiController]
[Route("api")]
public sealed class HealthController : ControllerBase
{
    private readonly DeployAIDbContext _db;
    private readonly IConfiguration _configuration;

    public HealthController(DeployAIDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    /// <summary>Basic liveness check - always returns ok if the API process is running.</summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "ok", service = "DeployAI" });
    }

    /// <summary>
    /// Reports connectivity status for each configured hosting provider (Vercel/Railway/Coolify).
    /// </summary>
    [HttpGet("health/providers")]
    public async Task<IActionResult> ProviderHealth(CancellationToken cancellationToken)
    {
        var healthService = HttpContext.RequestServices.GetRequiredService<IProviderHealthService>();
        var summary = await healthService.GetSummaryAsync(cancellationToken);
        return Ok(new
        {
            providers = summary.Providers.Select(p => new
            {
                name = p.Name,
                status = p.Status,
                message = p.Message
            })
        });
    }

    /// <summary>
    /// Checks database connectivity and reports applied/pending EF Core migrations plus row counts
    /// for the core tables. Returns HTTP 503 (rather than throwing) if the database is unreachable.
    /// </summary>
    [HttpGet("health/db")]
    public async Task<IActionResult> DatabaseHealth(CancellationToken cancellationToken)
    {
        try
        {
            if (!await _db.Database.CanConnectAsync(cancellationToken))
            {
                return StatusCode(503, new { status = "unavailable", message = "Could not connect to the database." });
            }

            var appliedMigrations = new List<string>();
            var pendingMigrations = new List<string>();
            if (_db.Database.IsRelational())
            {
                appliedMigrations = (await _db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
                pendingMigrations = (await _db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            }

            return Ok(new
            {
                status = "ok",
                database = ResolveDatabaseName(),
                migrationsApplied = appliedMigrations,
                pendingMigrations,
                tables = new Dictionary<string, int>
                {
                    ["users"] = await _db.Users.CountAsync(cancellationToken),
                    ["projects"] = await _db.Projects.CountAsync(cancellationToken),
                    ["provider_credentials"] = await _db.ProviderCredentials.CountAsync(cancellationToken),
                    ["deploy_targets"] = await _db.DeployTargets.CountAsync(cancellationToken),
                    ["deployments"] = await _db.Deployments.CountAsync(cancellationToken),
                    ["deployment_targets"] = await _db.DeploymentTargets.CountAsync(cancellationToken),
                    ["deployment_logs"] = await _db.DeploymentLogs.CountAsync(cancellationToken),
                    ["refresh_tokens"] = await _db.RefreshTokens.CountAsync(cancellationToken)
                }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { status = "error", message = ex.Message });
        }
    }

    private string? ResolveDatabaseName()
    {
        var connectionString = _configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString).Database;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
