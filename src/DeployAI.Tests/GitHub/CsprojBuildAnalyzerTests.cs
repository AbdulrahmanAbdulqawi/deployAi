using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.GitHub;

public class CsprojBuildAnalyzerTests
{
    [Fact]
    public void HasSiblingProjectReferences_ReturnsTrue_WhenCsprojReferencesParent()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <ProjectReference Include="..\DeployAI.Core\DeployAI.Core.csproj" />
              </ItemGroup>
            </Project>
            """;

        Assert.True(CsprojBuildAnalyzer.HasSiblingProjectReferences(csproj));
    }

    [Theory]
    [InlineData("src/DeployAI.Api", "src")]
    [InlineData("My.Api", "")]
    public void ResolveBuildRootDirectory_ReturnsParentDirectory(string projectDirectory, string expectedRoot)
    {
        Assert.Equal(expectedRoot, CsprojBuildAnalyzer.ResolveBuildRootDirectory(projectDirectory));
    }

    [Fact]
    public void BuildPublishCommand_ReturnsRelativePublishCommand_ForMonorepoLayout()
    {
        var command = CsprojBuildAnalyzer.BuildPublishCommand("src", "src/DeployAI.Api");

        Assert.Equal("dotnet publish DeployAI.Api/DeployAI.Api.csproj -c Release -o out", command);
    }

    [Fact]
    public void BuildPublishCommand_ReturnsNull_ForStandaloneProject()
    {
        Assert.Null(CsprojBuildAnalyzer.BuildPublishCommand("My.Api", "My.Api"));
    }
}
