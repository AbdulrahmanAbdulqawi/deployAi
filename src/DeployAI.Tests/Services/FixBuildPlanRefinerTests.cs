using DeployAI.Api.Services;

namespace DeployAI.Tests.Services;

public class FixBuildPlanRefinerTests
{
    [Fact]
    public void Refine_FindsCsprojUnderSrcWhenServiceDirectoryMissing()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"deployai-refine-{Guid.NewGuid():N}");
        var projectDir = Path.Combine(repoRoot, "src", "TicketHub.Server");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "TicketHub.Server.csproj"), "<Project />");

        try
        {
            var initial = new FixBuildPlan(
                "TicketHub.Server",
                null,
                "dotnet build \"./TicketHub.Server/TicketHub.Server.csproj\"");

            var refined = FixBuildPlanRefiner.Refine(repoRoot, initial);

            Assert.Equal("src/TicketHub.Server", refined.WorkingDirectory);
            Assert.Equal("dotnet build \"TicketHub.Server.csproj\"", refined.BuildCommand);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Refine_KeepsPlanWhenProjectAlreadyExists()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"deployai-refine-{Guid.NewGuid():N}");
        var projectDir = Path.Combine(repoRoot, "TicketHub.Server");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "TicketHub.Server.csproj"), "<Project />");

        try
        {
            var initial = new FixBuildPlan("TicketHub.Server", null, "dotnet build \"TicketHub.Server.csproj\"");

            var refined = FixBuildPlanRefiner.Refine(repoRoot, initial);

            Assert.Equal("TicketHub.Server", refined.WorkingDirectory);
            Assert.Equal("dotnet build \"TicketHub.Server.csproj\"", refined.BuildCommand);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }
}
