using DeployAI.Infrastructure.GitHub;
using Moq;

namespace DeployAI.Tests.GitHub;

public class RepositoryClassifierTests
{
    private readonly Mock<IGitHubService> _gitHub = new();
    private readonly FrontendBuildDetector _frontendDetector = new();
    private readonly ServerBuildDetector _serverDetector = new();
    private readonly DatabaseRequirementDetector _databaseDetector = new();

    [Fact]
    public async Task ClassifyAsync_StaticSite_ReturnsWebsiteOnVercelWithHighConfidence()
    {
        SetupRootFiles(["index.html"]);
        SetupFile("index.html", "<html></html>");

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Single(plan.Parts);
        Assert.Equal("website", plan.Parts[0].Role);
        Assert.Equal("vercel", plan.Parts[0].ProviderName);
        Assert.Contains("global hosting", plan.PlainSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassifyAsync_DotNetServer_ReturnsRailwayWithHighConfidence()
    {
        SetupRootContents(["My.Api"]);
        SetupDirectoryContents("My.Api", ["My.Api.csproj"]);
        SetupFile("My.Api/My.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Single(plan.Parts);
        Assert.Equal("server", plan.Parts[0].Role);
        Assert.Equal("railway", plan.Parts[0].ProviderName);
        Assert.Equal("dotnet", plan.Parts[0].Framework);
    }

    [Fact]
    public async Task ClassifyAsync_Monorepo_ReturnsSplitPlanWithHighConfidence()
    {
        SetupRootContents(["client", "My.Api"]);
        SetupDirectoryContents("client", ["angular.json", "package.json"]);
        SetupDirectoryContents("My.Api", ["My.Api.csproj"]);
        SetupFile("client/angular.json", """
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
        SetupFile("client/package.json", """{ "dependencies": { "@angular/core": "19.0.0" } }""");
        SetupFile("My.Api/My.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Equal(2, plan.Parts.Count);
        Assert.Contains(plan.Parts, part => part.Role == "website" && part.ProviderName == "vercel");
        Assert.Contains(plan.Parts, part => part.Role == "server" && part.ProviderName == "railway");
    }

    [Fact]
    public async Task ClassifyAsync_DeployAiLayout_ReturnsClientWebsiteAndNestedDotnetApi()
    {
        SetupRootContents(["client", "src"]);
        SetupDirectoryContents("client", ["angular.json", "package.json"]);
        SetupDirectorySubdirectories("src", ["DeployAI.Api", "DeployAI.Core"]);
        SetupDirectoryContents("src/DeployAI.Api", ["DeployAI.Api.csproj"]);
        SetupDirectoryContents("src/DeployAI.Core", ["DeployAI.Core.csproj"]);
        SetupFile("client/angular.json", """
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
        SetupFile("client/package.json", """
            {
              "dependencies": { "@angular/core": "18.0.0" },
              "scripts": { "start": "ng serve", "build": "ng build" }
            }
            """);
        SetupFile("src/DeployAI.Api/DeployAI.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <ProjectReference Include="..\DeployAI.Core\DeployAI.Core.csproj" />
              </ItemGroup>
            </Project>
            """);
        SetupFile("src/DeployAI.Core/DeployAI.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Equal(2, plan.Parts.Count);

        var website = plan.Parts.Single(part => part.Role == "website");
        Assert.Equal("vercel", website.ProviderName);
        Assert.Equal("client", website.RootDirectory);
        Assert.Equal("angular", website.Framework);

        var server = plan.Parts.Single(part => part.Role == "server");
        Assert.Equal("railway", server.ProviderName);
        Assert.Equal("src", server.RootDirectory);
        Assert.Equal("src/DeployAI.Api", server.ServiceDirectory);
        Assert.Equal("dotnet", server.Framework);
        Assert.Equal("dotnet publish DeployAI.Api/DeployAI.Api.csproj -c Release -o out", server.BuildCommand);
    }

    [Fact]
    public async Task ClassifyAsync_ServerWithPostgres_IncludesDatabasePart()
    {
        SetupRootContents(["api"]);
        SetupDirectoryContents("api", ["package.json", "appsettings.json"]);
        SetupFile("api/package.json", """{ "scripts": { "start": "node index.js" } }""");
        SetupFile("api/appsettings.json", """
            {
              "ConnectionStrings": {
                "DefaultConnection": "Host=localhost;Database=app;"
              }
            }
            """);
        SetupFile("docker-compose.yml", """
            services:
              postgres:
                image: postgres:16
            """);

        var plan = await ClassifyAsync();

        Assert.Equal("high", plan.Confidence);
        Assert.Contains(plan.Parts, part => part.Role == "server");
        Assert.Contains(plan.Parts, part => part.Role == "database" && part.DatabaseEngine == "postgres");
    }

    [Fact]
    public async Task ClassifyAsync_EmptyRepo_ReturnsLowConfidenceQuestion()
    {
        SetupRootContents(["docs"]);
        SetupDirectoryContents("docs", ["README.md"]);

        var plan = await ClassifyAsync();

        Assert.Equal("low", plan.Confidence);
        Assert.Empty(plan.Parts);
        Assert.NotNull(plan.ClarifyingQuestion);
        Assert.Equal(2, plan.ClarifyingQuestion!.Options.Count);
    }

    [Fact]
    public async Task ClassifyAsync_AmbiguousReactApp_ReturnsLowConfidenceQuestion()
    {
        SetupRootFiles(["package.json"]);
        SetupDirectoryContents(string.Empty, ["package.json"]);
        SetupFile("package.json", """
            {
              "dependencies": {
                "react": "18.0.0",
                "vite": "5.0.0"
              },
              "scripts": {
                "build": "vite build"
              }
            }
            """);

        var plan = await ClassifyAsync();

        Assert.Equal("low", plan.Confidence);
        Assert.NotNull(plan.ClarifyingQuestion);
    }

    [Fact]
    public void BuildPlainSummary_Monorepo_MentionsWebsiteAndServer()
    {
        var website = new DeployAI.Core.Deployments.FrontendBuildProfile("client", "npm run build", "npm install", "dist/client/browser", "angular");
        var server = new DeployAI.Core.Deployments.ServerBuildProfile("api", null, null, null, "dotnet");
        var database = new DeployAI.Core.Deployments.DatabaseRequirementProfile(true, false, ["DefaultConnection"]);

        var summary = RepositoryClassifier.BuildPlainSummary(website, server, database, true, true);

        Assert.Contains("site on fast global hosting", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("database", summary, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<DeployAI.Core.Deployments.DeploymentPlan> ClassifyAsync()
    {
        var websiteDiscovery = new WebsiteBuildProfileDiscovery(_gitHub.Object, _frontendDetector);
        var serverDiscovery = new ServerBuildProfileDiscovery(_gitHub.Object, _serverDetector);
        var classifier = new RepositoryClassifier(
            _gitHub.Object,
            websiteDiscovery,
            serverDiscovery,
            _databaseDetector);

        return await classifier.ClassifyAsync("token", "owner", "repo", "main", CancellationToken.None);
    }

    private void SetupRootContents(IReadOnlyList<string> entries)
    {
        var items = entries
            .Select(name => new GitHubContentItem(name, name, "dir"))
            .ToList();

        _gitHub.Setup(service => service.ListAllContentsAsync(
                "token", "owner", "repo", string.Empty, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
    }

    private void SetupRootFiles(IReadOnlyList<string> fileNames)
    {
        var items = fileNames
            .Select(name => new GitHubContentItem(name, name, "file"))
            .ToList();

        _gitHub.Setup(service => service.ListAllContentsAsync(
                "token", "owner", "repo", string.Empty, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
    }

    private void SetupDirectoryContents(string path, IReadOnlyList<string> fileNames)
    {
        var normalizedPath = string.IsNullOrEmpty(path) ? string.Empty : path.Trim('/');
        var items = fileNames
            .Select(name => new GitHubContentItem(
                name,
                string.IsNullOrEmpty(normalizedPath) ? name : $"{normalizedPath}/{name}",
                "file"))
            .ToList();

        _gitHub.Setup(service => service.ListAllContentsAsync(
                "token", "owner", "repo", normalizedPath, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
    }

    private void SetupDirectorySubdirectories(string path, IReadOnlyList<string> directoryNames)
    {
        var normalizedPath = string.IsNullOrEmpty(path) ? string.Empty : path.Trim('/');
        var items = directoryNames
            .Select(name => new GitHubContentItem(
                name,
                string.IsNullOrEmpty(normalizedPath) ? name : $"{normalizedPath}/{name}",
                "dir"))
            .ToList();

        _gitHub.Setup(service => service.ListAllContentsAsync(
                "token", "owner", "repo", normalizedPath, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
    }

    private void SetupFile(string path, string content)
    {
        _gitHub.Setup(service => service.GetFileContentAsync(
                "token", "owner", "repo", path, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
    }
}
