using DeployAI.Api.Services;

namespace DeployAI.Tests.Services;

public class BuildErrorActionHintsTests
{
    [Fact]
    public void BuildSection_ExtractsMissingAngularRootProperty()
    {
        const string error = """
            Workspace config file cannot be loaded: /src/tickethub.client/angular.json
                Project "tickethub" is missing a required property "root".
            """;

        var section = BuildErrorActionHints.BuildSection(error);

        Assert.NotNull(section);
        Assert.Contains("Required fix", section);
        Assert.Contains("\"root\": \"\"", section);
        Assert.Contains("project \"tickethub\"", section);
        Assert.Contains("Do not change outputPath", section);
    }
}
