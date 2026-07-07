using DeployAI.Api.Services;

using DeployAI.Core.Deployments;



namespace DeployAI.Tests.Services;



public class ClaudeDeploymentPromptsTests

{

    [Fact]

    public void BuildFixPrompt_IncludesContextErrorAndOutputFormat()

    {

        var analysis = new DeploymentFailureAnalysis(
            DeploymentFailureCategory.CodeBuild,
            "Build failed with 2 errors",
            "error TS2304: Cannot find name 'Foo'.\nerror TS2304: Cannot find name 'Bar'.",
            ["src/app/a.component.ts", "src/app/b.component.ts"],
            true,
            2);

        var prompt = ClaudeDeploymentPrompts.BuildFixPrompt(
            "owner",
            "repo",
            "abc123",
            "Vercel",
            "Angular",
            analysis);

        Assert.Contains("Repository: owner/repo @ abc123", prompt);
        Assert.Contains("Failed provider: Vercel", prompt);
        Assert.Contains("Framework: Angular", prompt);
        Assert.Contains("error TS2304", prompt);
        Assert.Contains("Build errors (2 distinct errors)", prompt);
        Assert.Contains("Fix ALL 2 errors", prompt);
        Assert.Contains("run_local_build", prompt);
        Assert.Contains("Build verification (required)", prompt);
        Assert.Contains("src/app/a.component.ts", prompt);
        Assert.Contains("src/app/b.component.ts", prompt);
        Assert.Contains("github_read_file", prompt);
        Assert.Contains("submit_deployment_files", prompt);

    }



    [Fact]

    public void BuildMissingFilesPrompt_IncludesSplitOriginChecklistAndTools()

    {

        var parts = new List<DeploymentPlanPart>

        {

            new("website", "vercel", RootDirectory: "client", Framework: "Angular"),

            new("server", "railway", ServiceDirectory: "src/Api", Framework: "AspNetCore", DockerfilePath: "src/Api/Dockerfile")

        };



        var missing = new List<MissingDeploymentFile>

        {

            new("client/vercel.json", "SPA routing config missing", DeploymentFileSeverity.Blocking),

            new("src/Api/Program.cs", "CORS missing", DeploymentFileSeverity.Recommended)

        };



        var prompt = ClaudeDeploymentPrompts.BuildMissingFilesPrompt(

            "owner",

            "repo",

            "main",

            parts,

            missing);



        Assert.Contains("Role: website, Provider: vercel", prompt);

        Assert.Contains("Role: server, Provider: railway", prompt);

        Assert.Contains("[Blocking] client/vercel.json", prompt);

        Assert.Contains("[Recommended] src/Api/Program.cs", prompt);

        Assert.Contains("Split-origin checklist", prompt);

        Assert.Contains("github_read_file", prompt);

        Assert.Contains("AllowAnyOrigin()", prompt);

        Assert.Contains("docs/DEPLOYMENT.md", prompt);

        Assert.Contains("no /api or /hubs proxy rewrites", prompt);

    }



    [Fact]

    public void BuildMissingFilesPrompt_UsesGenericRulesForSinglePartPlan()

    {

        var parts = new List<DeploymentPlanPart>

        {

            new("website", "vercel", RootDirectory: "client", Framework: "NextJs")

        };



        var missing = new List<MissingDeploymentFile>

        {

            new("client/vercel.json", "Host config missing", DeploymentFileSeverity.Blocking)

        };



        var prompt = ClaudeDeploymentPrompts.BuildMissingFilesPrompt(

            "owner",

            "repo",

            "main",

            parts,

            missing);



        Assert.Contains("single-part deployment", prompt);

        Assert.DoesNotContain("Split-origin checklist", prompt);

    }

}


