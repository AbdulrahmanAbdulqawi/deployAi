using DeployAI.Api.Services;
using DeployAI.Core.Deployments;

namespace DeployAI.Tests.Services;

public class DeploymentFailureAnalyzerTests
{
    [Fact]
    public void Analyze_TypeScriptError_ClassifiesAsCodeBuild()
    {
        var analyzer = new DeploymentFailureAnalyzer();
        var logs = new[]
        {
            "Running npm run build",
            "src/app/app.component.ts:12:5 - error TS2304: Cannot find name 'foo'.",
            "Build failed."
        };

        var result = analyzer.Analyze("vercel", logs);

        Assert.Equal(DeploymentFailureCategory.CodeBuild, result.Category);
        Assert.True(result.CanRequestClaudeFix);
        Assert.Contains("src/app/app.component.ts", result.ReferencedFiles);
    }

    [Fact]
    public void Analyze_InfrastructureMessage_DoesNotOfferClaudeFix()
    {
        var analyzer = new DeploymentFailureAnalyzer();
        var logs = new[]
        {
            "This Vercel app is not linked to GitHub. Reconnect the repository in Vercel, then try publishing again."
        };

        var result = analyzer.Analyze("vercel", logs);

        Assert.Equal(DeploymentFailureCategory.Infrastructure, result.Category);
        Assert.False(result.CanRequestClaudeFix);
    }

    [Fact]
    public void Analyze_CSharpError_ClassifiesAsCodeBuild()
    {
        var analyzer = new DeploymentFailureAnalyzer();
        var logs = new[]
        {
            "dotnet build",
            "Program.cs(42,9): error CS1002: ; expected"
        };

        var result = analyzer.Analyze("railway", logs);

        Assert.Equal(DeploymentFailureCategory.CodeBuild, result.Category);
        Assert.True(result.CanRequestClaudeFix);
    }

    [Fact]
    public void Analyze_MultipleTypeScriptErrors_IncludesAllInExcerptAndCountsErrors()
    {
        var analyzer = new DeploymentFailureAnalyzer();
        var logs = new[]
        {
            "Running npm run build",
            "src/app/a.component.ts:1:1 - error TS2304: Cannot find name 'A'.",
            "src/app/b.component.ts:2:3 - error TS2304: Cannot find name 'B'.",
            "src/app/c.component.ts:3:5 - error TS2304: Cannot find name 'C'.",
            "Build failed."
        };

        var result = analyzer.Analyze("vercel", logs);

        Assert.Equal(DeploymentFailureCategory.CodeBuild, result.Category);
        Assert.Equal(4, result.ErrorCount);
        Assert.Contains("4 errors", result.Summary);
        Assert.NotNull(result.ErrorExcerpt);
        Assert.Contains("a.component.ts", result.ErrorExcerpt);
        Assert.Contains("b.component.ts", result.ErrorExcerpt);
        Assert.Contains("c.component.ts", result.ErrorExcerpt);
        Assert.Equal(3, result.ReferencedFiles.Count);
    }

    [Fact]
    public void AnalyzeForFix_IncludesMoreErrorLinesThanStandardAnalyze()
    {
        var analyzer = new DeploymentFailureAnalyzer();
        var logs = Enumerable.Range(1, 150)
            .Select(i => $"src/app/file{i}.ts:1:1 - error TS2304: Cannot find name 'X{i}'.")
            .Concat(["Build failed."])
            .ToArray();

        var standard = analyzer.Analyze("vercel", logs);
        var fix = analyzer.AnalyzeForFix("vercel", logs);

        Assert.NotNull(standard.ErrorExcerpt);
        Assert.NotNull(fix.ErrorExcerpt);
        Assert.Contains("additional error lines omitted", standard.ErrorExcerpt);
        Assert.DoesNotContain("additional error lines omitted", fix.ErrorExcerpt);
        Assert.True(fix.ErrorExcerpt!.Length > standard.ErrorExcerpt!.Length);
    }

    [Fact]
    public void Analyze_AngularEsbuildErrorFormat_ExtractsFilesAndExcerpt()
    {
        var analyzer = new DeploymentFailureAnalyzer();
        var logs = new[]
        {
            "\u2714 Building...",
            "Application bundle generation failed. [20.091 seconds]",
            "\u2718 [ERROR] TS2339: Property 'apiUrl' does not exist on type '{ production: boolean; apiBaseUrl: string; }'. [plugin angular-compiler]",
            "    src/app/core/admin.service.ts:65:34:",
            "      65 \u2502   private apiUrl = `${environment.apiUrl}/admin`;",
            "\u2718 [ERROR] TS2339: Property 'apiUrl' does not exist. [plugin angular-compiler]",
            "    src/app/core/analytics.service.ts:64:34:",
            "Error: Command \"node scripts/write-api-env.mjs && npm run build\" exited with 1"
        };

        var result = analyzer.Analyze("vercel", logs);

        Assert.Equal(DeploymentFailureCategory.CodeBuild, result.Category);
        Assert.True(result.CanRequestClaudeFix);
        Assert.Contains("src/app/core/admin.service.ts", result.ReferencedFiles);
        Assert.Contains("src/app/core/analytics.service.ts", result.ReferencedFiles);
        Assert.NotNull(result.ErrorExcerpt);
        Assert.Contains("TS2339", result.ErrorExcerpt);
        Assert.Contains("admin.service.ts", result.ErrorExcerpt);
    }

    [Fact]
    public void Analyze_VercelMissingSecretReference_OffersClaudeFix()
    {
        var analyzer = new DeploymentFailureAnalyzer();
        var logs = new[]
        {
            "Deploying...",
            "Environment Variable \"API_BASE_URL\" references Secret \"api_base_url\", which does not exist."
        };

        var result = analyzer.Analyze("vercel", logs);

        Assert.True(result.CanRequestClaudeFix);
        Assert.Contains("vercel.json", result.ReferencedFiles);
        Assert.NotNull(result.ErrorExcerpt);
        Assert.Contains("references Secret", result.ErrorExcerpt);
    }

    [Fact]
    public void Analyze_ExcludesWarningsFromErrorExcerpt()
    {
        var analyzer = new DeploymentFailureAnalyzer();
        var logs = new[]
        {
            "Running dotnet build",
            "src/TicketHub.Server/Program.cs(10,1): warning CS8618: Non-nullable field 'x' must contain a non-null value.",
            "src/TicketHub.Server/Program.cs(12,5): error CS0246: The type or namespace name 'MissingType' could not be found",
            "Build failed."
        };

        var result = analyzer.Analyze("railway", logs);

        Assert.NotNull(result.ErrorExcerpt);
        Assert.Contains("error CS0246", result.ErrorExcerpt);
        Assert.Contains("Build failed", result.ErrorExcerpt);
        Assert.DoesNotContain("warning CS8618", result.ErrorExcerpt);
    }

    [Fact]
    public void IsWarningLine_DetectsCommonWarningFormats()
    {
        Assert.True(DeploymentFailureAnalyzer.IsWarningLine("warning CS8618: field is null"));
        Assert.True(DeploymentFailureAnalyzer.IsWarningLine("src/app.ts:10:1 - warning TS1234: something"));
        Assert.False(DeploymentFailureAnalyzer.IsWarningLine("src/app.ts:10:1 - error TS1234: something"));
    }
}
