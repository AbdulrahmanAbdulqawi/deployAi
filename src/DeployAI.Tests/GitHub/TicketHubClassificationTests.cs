using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.GitHub;
using Moq;

namespace DeployAI.Tests.GitHub;

/// <summary>
/// TicketHub's real layout, because the plan it produced offered no database — for an app whose
/// startup runs migrations against <c>ConnectionStrings:DefaultConnection</c>.
///
/// Nothing was broken in isolation. There is no compose file and no Prisma schema, the committed
/// connection string is <c>""</c> (which is the correct, secret-free thing to commit), and the
/// project that holds it sits one level below the build root because the API references four
/// sibling projects. Every signal DeployAI read was genuinely empty. The one signal that is never
/// empty — the driver package in the project file — was not read at all.
/// </summary>
public class TicketHubClassificationTests
{
    private const string Token = "token";
    private const string Owner = "owner";
    private const string Repo = "TicketHub";
    private const string Branch = "master";

    private readonly Mock<IGitHubService> _gitHub = new();

    /// <summary>Angular client, a solution at the root, the API beside its class libraries.</summary>
    private void SetupTicketHub(string serverPackages)
    {
        StubEntries(string.Empty,
            [("tickethub.client", "dir"), ("TicketHub.Server", "dir"), ("TicketHub.Data", "dir"),
             ("TicketHub.sln", "file"), ("NuGet.config", "file")]);

        StubFile("TicketHub.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "TicketHub.Data", "TicketHub.Data\TicketHub.Data.csproj", "{A1}"
            EndProject
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "TicketHub.Server", "TicketHub.Server\TicketHub.Server.csproj", "{A2}"
            EndProject
            """);

        StubEntries("tickethub.client", [("angular.json", "file"), ("package.json", "file")]);
        StubFile("tickethub.client/angular.json",
            """{ "projects": { "client": { "architect": { "build": { "options": { "outputPath": "dist/tickethub.client/browser" } } } } } }""");
        StubFile("tickethub.client/package.json",
            """{ "dependencies": { "@angular/core": "^20.0.0" }, "scripts": { "build": "ng build" } }""");

        StubEntries("TicketHub.Server",
            [("TicketHub.Server.csproj", "file"), ("appsettings.json", "file"), ("Program.cs", "file")]);
        StubFile("TicketHub.Server/TicketHub.Server.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
            {serverPackages}
                <ProjectReference Include="..\TicketHub.Data\TicketHub.Data.csproj" />
              </ItemGroup>
            </Project>
            """);
        // The committed connection string is empty, because a real one would be a secret.
        StubFile("TicketHub.Server/appsettings.json", """{ "ConnectionStrings": { "DefaultConnection": "" } }""");

        StubEntries("TicketHub.Data", [("TicketHub.Data.csproj", "file")]);
        StubFile("TicketHub.Data/TicketHub.Data.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
    }

    private const string NpgsqlPackage =
        """    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.10" />""";

    private const string LoggingPackageOnly =
        """    <PackageReference Include="Serilog.AspNetCore" Version="8.0.0" />""";

    [Fact]
    public async Task ClassifyAsync_OffersThePostgresTheProjectFileAsksFor()
    {
        SetupTicketHub(NpgsqlPackage);

        var plan = await ClassifyAsync();

        Assert.Contains(plan.Parts, part => part.Role == "database" && part.DatabaseEngine == "postgres");
    }

    /// <summary>
    /// The plan is what the user reads before agreeing to it, so it has to say the database is
    /// coming — not provision one silently, and not leave them to know they needed it.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_SaysTheDatabaseIsPartOfThePlan()
    {
        SetupTicketHub(NpgsqlPackage);

        var plan = await ClassifyAsync();

        Assert.Contains("needs a database", plan.PlainSummary);
    }

    /// <summary>
    /// The same repository without a database driver gets no database. Guessing one provisions a
    /// resource the app never opens, and bills for it.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_OffersNoDatabase_WhenTheProjectReferencesNoDriver()
    {
        SetupTicketHub(LoggingPackageOnly);

        var plan = await ClassifyAsync();

        Assert.DoesNotContain(plan.Parts, part => part.Role == "database");
    }

    private async Task<DeploymentPlan> ClassifyAsync()
    {
        var layout = new RepositoryLayoutResolver(_gitHub.Object);
        var classifier = new RepositoryClassifier(
            _gitHub.Object,
            new WebsiteBuildProfileDiscovery(_gitHub.Object, new FrontendBuildDetector()),
            new ServerBuildProfileDiscovery(_gitHub.Object, new ServerBuildDetector(), layout),
            new DatabaseRequirementDetector(),
            new DotnetProjectLocator(_gitHub.Object),
            layout,
            layout);

        return await classifier.ClassifyAsync(
            Token, Owner, Repo, Branch, new RepositoryClassificationOptions(true), CancellationToken.None);
    }

    private void StubEntries(string path, (string Name, string Type)[] entries)
    {
        var items = entries
            .Select(entry => new GitHubContentItem(
                entry.Name, string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}", entry.Type))
            .ToList();

        _gitHub.Setup(g => g.ListAllContentsAsync(Token, Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
        _gitHub.Setup(g => g.ListContentsAsync(Token, Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items.Where(i => i.Type == "dir").ToList());
    }

    private void StubFile(string path, string content) =>
        _gitHub.Setup(g => g.GetFileContentAsync(Token, Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
}
