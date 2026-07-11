using System.Text.Json;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

public enum ProjectHealthStatus
{
    Healthy,
    Degraded,
    Down,
    Unknown
}

public sealed class ProjectHealthState
{
    public DateTimeOffset LastCheckedAt { get; set; }
    public ProjectHealthStatus Status { get; set; } = ProjectHealthStatus.Unknown;
    public int PassedChecks { get; set; }
    public int TotalChecks { get; set; }
    public string? Summary { get; set; }
    public Guid? DeploymentId { get; set; }

    public static ProjectHealthState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ProjectHealthState>(json);
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}

public sealed class ProjectHealthMonitorJob
{
    private readonly DeployAIDbContext _db;
    private readonly IDeploymentVerificationService _verificationService;
    private readonly AppOptions _appOptions;

    public ProjectHealthMonitorJob(
        DeployAIDbContext db,
        IDeploymentVerificationService verificationService,
        IOptions<AppOptions> appOptions)
    {
        _db = db;
        _verificationService = verificationService;
        _appOptions = appOptions.Value;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var projectIds = await _db.Projects
            .AsNoTracking()
            .Where(p => p.DeployTargets.Any())
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        foreach (var projectId in projectIds)
        {
            await CheckProjectAsync(projectId, cancellationToken);
        }
    }

    private async Task CheckProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var latestDeployment = await _db.Deployments
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Status == DeploymentStatuses.Success)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestDeployment is null)
        {
            return;
        }

        var project = await _db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
        var result = await _verificationService.VerifyAsync(
            latestDeployment.Id,
            DeploymentVerificationScope.Both,
            cancellationToken);

        var passed = result.Checks.Count(c => c.Status == "passed");
        var total = result.Checks.Count;
        var failed = result.Checks.Count(c => c.Status == "failed");

        var status = failed switch
        {
            0 when passed > 0 => ProjectHealthStatus.Healthy,
            0 => ProjectHealthStatus.Unknown,
            var count when count == total => ProjectHealthStatus.Down,
            _ => ProjectHealthStatus.Degraded
        };

        project.HealthJson = new ProjectHealthState
        {
            LastCheckedAt = DateTimeOffset.UtcNow,
            Status = status,
            PassedChecks = passed,
            TotalChecks = total,
            Summary = result.Success ? "All checks passed." : $"{failed} of {total} checks failed.",
            DeploymentId = latestDeployment.Id
        }.ToJson();
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
