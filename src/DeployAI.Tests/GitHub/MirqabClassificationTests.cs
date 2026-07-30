using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.GitHub;
using Moq;

namespace DeployAI.Tests.GitHub;

/// <summary>
/// Mirqab's real layout, because its first deploy produced a website target and nothing else — no
/// server and no database — while the repository holds a Microsoft.NET.Sdk.Web project and a compose
/// file with Postgres in it. Either the wizard was told to deploy only the site, or classification
/// cannot see this shape; these decide which.
/// </summary>
public class MirqabClassificationTests
{
    private const string Token = "token";
    private const string Owner = "owner";
    private const string Repo = "Mirqab";
    private const string Branch = "master";

    private readonly Mock<IGitHubService> _gitHub = new();

    /// <summary>
    /// Angular in client/, a .NET solution in src/ with the API beside five class libraries, and
    /// tests/ and docs/ alongside — as of 3969669.
    /// </summary>
    private void SetupMirqab()
    {
        StubDirs(string.Empty, "client", "docs", "src", "tests");

        StubFiles("client", "angular.json", "package.json", "Dockerfile", "proxy.conf.json");
        StubFile("client/angular.json", """
            {
              "projects": {
                "client": {
                  "architect": {
                    "build": {
                      "builder": "@angular-devkit/build-angular:application",
                      "options": { "outputPath": "dist/client" }
                    }
                  }
                }
              }
            }
            """);
        StubFile("client/package.json", """
            {
              "name": "client",
              "dependencies": { "@angular/core": "^20.3.0" },
              "devDependencies": { "@angular/cli": "^20.3.32" },
              "scripts": { "build": "ng build", "start": "ng serve" }
            }
            """);

        StubDirs("src",
            "Mirqab.Api", "Mirqab.Application", "Mirqab.Core",
            "Mirqab.Data", "Mirqab.Providers.Coolify", "Mirqab.Providers.GitHub");
        StubFiles("src/Mirqab.Api", "Mirqab.Api.csproj");
        StubFile("src/Mirqab.Api/Mirqab.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <ProjectReference Include="..\Mirqab.Application\Mirqab.Application.csproj" />
              </ItemGroup>
            </Project>
            """);
        foreach (var library in (string[])
                 ["Mirqab.Application", "Mirqab.Core", "Mirqab.Data",
                  "Mirqab.Providers.Coolify", "Mirqab.Providers.GitHub"])
        {
            StubFiles($"src/{library}", $"{library}.csproj");
            StubFile($"src/{library}/{library}.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        }

        StubDirs("docs");
        StubDirs("tests");

        // Postgres lives in the compose file at the repository root, which is where Mirqab keeps it.
        StubFile("docker-compose.yml", """
            services:
              postgres:
                image: postgres:17
              api:
                build: .
            """);
    }

    [Fact]
    public async Task ClassifyAsync_OffersTheDotnetApiAlongsideTheAngularSite()
    {
        SetupMirqab();

        var plan = await ClassifyAsync();

        var server = Assert.Single(plan.Parts, part => part.Role == "server");
        Assert.Equal("dotnet", server.Framework);
        Assert.Equal("src/Mirqab.Api", server.ServiceDirectory);

        var website = Assert.Single(plan.Parts, part => part.Role == "website");
        Assert.Equal("angular", website.Framework);
        Assert.Equal("client", website.RootDirectory);
    }

    [Fact]
    public async Task ClassifyAsync_OffersThePostgresItsComposeFileAsksFor()
    {
        SetupMirqab();

        var plan = await ClassifyAsync();

        Assert.Contains(plan.Parts, part => part.Role == "database" && part.DatabaseEngine == "postgres");
    }

    [Fact]
    public async Task ClassifyAsync_DoesNotMistakeTheAngularClientForTheServer()
    {
        // client/ holds a package.json and sorts before src/. Ranking excludes known frontend
        // directory names for exactly this reason; without it the site would be deployed twice and
        // the API not at all.
        SetupMirqab();

        var plan = await ClassifyAsync();

        Assert.DoesNotContain(plan.Parts, part => part.Role == "server" && part.RootDirectory == "client");
    }

    private async Task<DeploymentPlan> ClassifyAsync()
    {
        var layout = new RepositoryLayoutResolver(_gitHub.Object);
        var classifier = new RepositoryClassifier(
            _gitHub.Object,
            new WebsiteBuildProfileDiscovery(_gitHub.Object, new FrontendBuildDetector()),
            new ServerBuildProfileDiscovery(_gitHub.Object, new ServerBuildDetector(), layout),
            new DatabaseRequirementDetector(),
            layout,
            layout);

        return await classifier.ClassifyAsync(
            Token, Owner, Repo, Branch, new RepositoryClassificationOptions(false), CancellationToken.None);
    }

    private void StubDirs(string path, params string[] names) =>
        _gitHub.Setup(g => g.ListAllContentsAsync(Token, Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(names
                .Select(name => new GitHubContentItem(
                    name, string.IsNullOrEmpty(path) ? name : $"{path}/{name}", "dir"))
                .ToList());

    private void StubFiles(string path, params string[] names) =>
        _gitHub.Setup(g => g.ListAllContentsAsync(Token, Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(names
                .Select(name => new GitHubContentItem(name, $"{path}/{name}", "file"))
                .ToList());

    private void StubFile(string path, string content) =>
        _gitHub.Setup(g => g.GetFileContentAsync(Token, Owner, Repo, path, Branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
}
