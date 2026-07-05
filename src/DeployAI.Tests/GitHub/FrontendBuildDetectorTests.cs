using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.GitHub;

public class FrontendBuildDetectorTests
{
    private readonly FrontendBuildDetector _detector = new();

    [Fact]
    public void Detect_ParsesAngularOutputPath_FromAngularJson()
    {
        const string angularJson = """
            {
              "projects": {
                "idaara.client": {
                  "architect": {
                    "build": {
                      "builder": "@angular-devkit/build-angular:browser",
                      "options": {
                        "outputPath": "dist/idaara.client"
                      }
                    }
                  }
                }
              }
            }
            """;

        var profile = _detector.Detect("idaara.client", angularJson, """
            { "dependencies": { "@angular/core": "19.0.0" } }
            """);

        Assert.Equal("idaara.client", profile.RootDirectory);
        Assert.Equal("dist/idaara.client", profile.OutputDirectory);
        Assert.Equal("angular", profile.Framework);
        Assert.Equal("npm run build", profile.BuildCommand);
        Assert.Equal("npm install", profile.InstallCommand);
    }

    [Fact]
    public void Detect_AppendsBrowser_ForApplicationBuilder()
    {
        const string angularJson = """
            {
              "projects": {
                "client": {
                  "architect": {
                    "build": {
                      "builder": "@angular-devkit/build-angular:application",
                      "options": {
                        "outputPath": "dist/client"
                      }
                    }
                  }
                }
              }
            }
            """;

        var profile = _detector.Detect("client", angularJson, null);

        Assert.Equal("dist/client/browser", profile.OutputDirectory);
    }

    [Fact]
    public void Detect_UsesClientFallback_WhenAngularJsonMissing()
    {
        var profile = _detector.Detect("client", null, null);

        Assert.Equal("client", profile.RootDirectory);
        Assert.Equal("dist/client/browser", profile.OutputDirectory);
    }

    [Fact]
    public void Detect_UsesFolderNameFallback_ForNonClientRoots()
    {
        var profile = _detector.Detect("idaara.client", null, null);

        Assert.Equal("idaara.client", profile.RootDirectory);
        Assert.Equal("dist/idaara.client", profile.OutputDirectory);
    }

    [Theory]
    [InlineData("""{ "dependencies": { "next": "14.0.0" } }""", "next", ".next")]
    [InlineData("""{ "dependencies": { "nuxt": "3.0.0" } }""", "nuxt", ".output/public")]
    [InlineData("""{ "dependencies": { "@sveltejs/kit": "2.0.0" } }""", "sveltekit", "build")]
    [InlineData("""{ "dependencies": { "astro": "4.0.0" } }""", "astro", "dist")]
    [InlineData("""{ "dependencies": { "vite": "5.0.0" } }""", "vite", "dist")]
    [InlineData("""{ "dependencies": { "react": "18.0.0" } }""", "react", "dist")]
    public void Detect_RecognizesCommonFrontendFrameworks(string packageJson, string framework, string outputDirectory)
    {
        var profile = _detector.Detect(string.Empty, null, packageJson);

        Assert.Equal(framework, profile.Framework);
        Assert.Equal(outputDirectory, profile.OutputDirectory);
    }

    [Fact]
    public void Detect_RecognizesAngular_FromAngularJsonWithoutPackageJson()
    {
        const string angularJson = """
            {
              "projects": {
                "app": {
                  "architect": {
                    "build": {
                      "options": { "outputPath": "dist/app" }
                    }
                  }
                }
              }
            }
            """;

        var profile = _detector.Detect(string.Empty, angularJson, null);

        Assert.Equal("angular", profile.Framework);
    }
}
