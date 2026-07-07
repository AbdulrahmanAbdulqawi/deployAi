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
        Assert.Contains("## Build Log", prompt);
        Assert.Contains("## Structured Errors", prompt);
        Assert.Contains("Distinct errors detected: 2", prompt);
        Assert.Contains("Fix ALL errors listed", prompt);
        Assert.Contains("run_command", prompt);
        Assert.Contains("### Verification", prompt);
        Assert.Contains("## What NOT to Do", prompt);
        Assert.Contains("src/app/a.component.ts", prompt);
        Assert.Contains("src/app/b.component.ts", prompt);
        Assert.Contains("write_file", prompt);
        Assert.Contains("read_file", prompt);
        Assert.Contains("submit_deployment_files", prompt);

    }



    [Fact]

    public void BuildMissingFilesPrompt_IncludesContextPlanGapsAndTools()

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



        Assert.Contains("You are a deployment engineer completing deployment setup for a repository.", prompt);

        Assert.Contains("Repository: owner/repo @ main", prompt);

        Assert.Contains("## Deployment Plan", prompt);

        Assert.Contains("\"ProviderName\": \"vercel\"", prompt);

        Assert.Contains("\"ProviderName\": \"railway\"", prompt);

        Assert.Contains("[Blocking] client/vercel.json", prompt);

        Assert.Contains("[Recommended] src/Api/Program.cs", prompt);

        Assert.Contains("github_read_file", prompt);

        Assert.Contains("submit_deployment_files", prompt);

        Assert.Contains("## Build Validation Step", prompt);

        Assert.Contains("## What NOT to Do", prompt);

    }



    [Fact]

    public void BuildMissingFilesPrompt_DescribesArchitectureForSinglePartPlan()

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


