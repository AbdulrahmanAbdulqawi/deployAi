using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Data.Entities;

namespace DeployAI.Tests.Services;

public class ProjectServiceMapperTests
{
    [Fact]
    public void MapProjectServices_GroupsApplicationAndDataServices()
    {
        var projectId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var postgresId = Guid.NewGuid();
        var vercelId = Guid.NewGuid();

        var project = new Project
        {
            Id = projectId,
            UserId = Guid.NewGuid(),
            Name = "iDaara",
            GitHubRepoFullName = "owner/repo",
            DefaultBranch = "main",
            DeployTargets =
            [
                new DeployTarget
                {
                    Id = vercelId,
                    ProjectId = projectId,
                    ProviderName = "vercel",
                    CredentialId = Guid.NewGuid(),
                    ProviderProjectId = "vercel_1",
                    ConfigJson = """{"role":"website","rootDirectory":"client"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = serverId,
                    ProjectId = projectId,
                    ProviderName = "railway",
                    CredentialId = Guid.NewGuid(),
                    ProviderProjectId = "svc_app|env_1",
                    ConfigJson = """{"role":"server","serviceDirectory":"server","includePostgres":true,"includeRedis":false}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new DeployTarget
                {
                    Id = postgresId,
                    ProjectId = projectId,
                    ProviderName = "railway",
                    CredentialId = Guid.NewGuid(),
                    ProviderProjectId = "svc_pg|env_1",
                    ConfigJson = """{"role":"database","databaseEngine":"postgres","linkedServiceName":"Postgres","railwayProjectId":"proj_1"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        };

        var result = ProjectServiceMapper.MapProjectServices(project);

        Assert.Single(result.ApplicationServices, service => service.Id == vercelId);
        Assert.Single(result.ApplicationServices, service => service.Id == serverId);
        Assert.Single(result.DataServices, service => service.Id == postgresId);
        Assert.True(result.HasManagedServer);
        Assert.True(result.IncludePostgres);
        Assert.False(result.IncludeRedis);
        Assert.Equal("Postgres", result.DataServices[0].DisplayName);
        Assert.Contains("ConnectionStrings__DefaultConnection", result.DataServices[0].LinkedConnectionKeys);
        Assert.Contains("ConnectionStrings__Default", result.DataServices[0].LinkedConnectionKeys);
    }
}
