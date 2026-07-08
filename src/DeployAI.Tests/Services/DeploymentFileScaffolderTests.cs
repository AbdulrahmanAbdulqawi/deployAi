using DeployAI.Api.Services;
using DeployAI.Api.Services.DeploymentTemplates;

namespace DeployAI.Tests.Services;

public class DeploymentFileScaffolderTests
{
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
