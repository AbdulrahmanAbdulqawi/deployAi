using DeployAI.Data.Entities;

namespace DeployAI.Tests.Services;

public class DeploymentDetailDurationTests
{
    [Fact]
    public void CompletedDeployment_ComputesDurationSeconds()
    {
        var startedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddMinutes(2).AddSeconds(15);

        var deployment = new Deployment
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Branch = "main",
            Status = DeploymentStatuses.Success,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };

        var duration = deployment.CompletedAt.HasValue && deployment.StartedAt.HasValue
            ? (int?)(deployment.CompletedAt.Value - deployment.StartedAt.Value).TotalSeconds
            : null;

        Assert.Equal(135, duration);
    }

    [Fact]
    public void InProgressDeployment_HasNullDurationSeconds()
    {
        var deployment = new Deployment
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Branch = "main",
            Status = DeploymentStatuses.InProgress,
            StartedAt = DateTimeOffset.UtcNow
        };

        var duration = deployment.CompletedAt.HasValue && deployment.StartedAt.HasValue
            ? (int?)(deployment.CompletedAt.Value - deployment.StartedAt.Value).TotalSeconds
            : null;

        Assert.Null(duration);
    }
}
