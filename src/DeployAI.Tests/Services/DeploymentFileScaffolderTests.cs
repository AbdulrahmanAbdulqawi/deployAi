using DeployAI.Api.Services;
using DeployAI.Api.Services.DeploymentTemplates;

namespace DeployAI.Tests.Services;

public class DeploymentFileScaffolderTests
{
    /// <summary>
    /// A compose file that both publishes host ports and omits a service is two findings about one
    /// path, and both resolve to the same template. ToDictionary threw on the second and killed the
    /// whole setup run mid-stream — the UI counted up to a minute with no error and no PR, because
    /// the response had already started and the error page could not be written over it.
    /// </summary>
    [Fact]
    public void ScaffoldMissingFiles_SeveralFindingsAboutOneFile_ProduceItOnce()
    {
        var scaffolder = new DeploymentFileScaffolder(
            new DeploymentTemplateResolver(new DeploymentTemplateCatalog()));

        var parts = new[]
        {
            new DeployAI.Core.Deployments.DeploymentPlanPart(
                "website", "coolify", "client", null, null, null, null, null, "angular"),
            new DeployAI.Core.Deployments.DeploymentPlanPart(
                "server", "coolify", "src/api", "src/api", null, null, null, null, "dotnet")
        };

        var missing = new[]
        {
            new DeployAI.Core.Deployments.MissingDeploymentFile(
                "docker-compose.coolify.yml",
                "must declare both an `api` and a `web` service.",
                DeployAI.Core.Deployments.DeploymentFileSeverity.Blocking),
            new DeployAI.Core.Deployments.MissingDeploymentFile(
                "docker-compose.coolify.yml",
                "it publishes host ports.",
                DeployAI.Core.Deployments.DeploymentFileSeverity.Blocking)
        };

        var generated = scaffolder.ScaffoldMissingFiles(parts, missing);

        Assert.Single(generated, file =>
            file.Path.Equals("docker-compose.coolify.yml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PatchAppConfig_PrependsInterceptor_WhenMissing()
    {
        const string existing = """
            import { ApplicationConfig } from '@angular/core';
            import { provideHttpClient, withInterceptors } from '@angular/common/http';
            import { authInterceptor } from './auth.interceptor';

            export const appConfig: ApplicationConfig = {
              providers: [
                provideHttpClient(withInterceptors([authInterceptor]))
              ]
            };
            """;

        var patched = DeploymentFileScaffolder.PatchAppConfig(existing);

        Assert.NotNull(patched);
        Assert.Contains("apiBaseInterceptor", patched, StringComparison.Ordinal);
        Assert.Contains("authInterceptor", patched, StringComparison.Ordinal);
    }

    [Fact]
    public void PatchAppConfig_ReturnsExisting_WhenInterceptorAlreadyRegistered()
    {
        const string existing = "provideHttpClient(withInterceptors([apiBaseInterceptor, authInterceptor]))";

        var patched = DeploymentFileScaffolder.PatchAppConfig(existing);

        Assert.Equal(existing, patched);
    }

    [Fact]
    public void PatchAngularJson_AddsFileReplacements_WhenMissing()
    {
        const string existing = """
            {
              "projects": {
                "app": {
                  "architect": {
                    "build": {
                      "configurations": {
                        "production": {
                          "optimization": true
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var patched = DeploymentFileScaffolder.PatchAngularJson(existing);

        Assert.NotNull(patched);
        Assert.Contains("fileReplacements", patched, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("environment.production.ts", patched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PatchAngularJson_ReturnsExisting_WhenFileReplacementsPresent()
    {
        const string existing = """{ "production": { "fileReplacements": [{ "with": "environment.production.ts" }] } }""";

        var patched = DeploymentFileScaffolder.PatchAngularJson(existing);

        Assert.Equal(existing, patched);
    }
}
