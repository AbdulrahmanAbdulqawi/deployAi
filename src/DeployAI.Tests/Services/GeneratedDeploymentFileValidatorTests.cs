using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;

namespace DeployAI.Tests.Services;

public class GeneratedDeploymentFileValidatorTests
{
    [Fact]
    public void ValidateOrThrow_AcceptsValidJsonFile()
    {
        GeneratedDeploymentFileValidator.ValidateOrThrow([
            ("angular.json", """{ "version": 1, "projects": {} }""")
        ]);
    }

    [Fact]
    public void ValidateOrThrow_RejectsInvalidJsonFile()
    {
        var ex = Assert.Throws<DeployAIException>(() =>
            GeneratedDeploymentFileValidator.ValidateOrThrow([
                ("angular.json", "{ not valid json }")
            ]));

        Assert.Equal("setup_generation_failed", ex.ErrorCode);
        Assert.Contains("angular.json", ex.Message);
    }

    [Fact]
    public void ValidateOrThrow_RejectsBudgetsNestedInsideFileReplacementsObject()
    {
        var ex = Assert.Throws<DeployAIException>(() =>
            GeneratedDeploymentFileValidator.ValidateOrThrow([
                ("angular.json", """
                    {
                      "projects": {
                        "app": {
                          "architect": {
                            "build": {
                              "configurations": {
                                "production": {
                                  "fileReplacements": [
                                    {
                                      "replace": "src/environments/environment.ts",
                                      "with": "src/environments/environment.production.ts",
                                      "budgets": []
                                    }
                                  ]
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                    """)
            ]));

        Assert.Equal("setup_generation_failed", ex.ErrorCode);
        Assert.Contains("budgets nested inside fileReplacements", ex.Message);
    }

    [Fact]
    public void ValidateOrThrow_AcceptsSiblingFileReplacementsAndBudgets()
    {
        GeneratedDeploymentFileValidator.ValidateOrThrow([
            ("angular.json", """
                {
                  "projects": {
                    "app": {
                      "architect": {
                        "build": {
                          "configurations": {
                            "production": {
                              "fileReplacements": [
                                {
                                  "replace": "src/environments/environment.ts",
                                  "with": "src/environments/environment.production.ts"
                                }
                              ],
                              "budgets": [
                                { "type": "initial", "maximumWarning": "500kb" }
                              ]
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """)
        ]);
    }

    [Fact]
    public void ValidateOrThrow_RejectsDuplicateOutputPathInAngularJson()
    {
        var ex = Assert.Throws<DeployAIException>(() =>
            GeneratedDeploymentFileValidator.ValidateOrThrow([
                ("angular.json", """{ "projects": { "a": { "outputPath": "dist/a", "outputPath": "dist/b" } } }""")
            ]));

        Assert.Equal("setup_generation_failed", ex.ErrorCode);
        Assert.Contains("duplicate outputPath", ex.Message);
    }

    [Fact]
    public void ValidateOrThrow_RejectsVercelJsonApiProxyRewrites()
    {
        var ex = Assert.Throws<DeployAIException>(() =>
            GeneratedDeploymentFileValidator.ValidateOrThrow([
                ("client/vercel.json", """
                    {
                      "rewrites": [
                        { "source": "/api/:path*", "destination": "https://api.example.com/api/:path*" }
                      ]
                    }
                    """)
            ]));

        Assert.Equal("setup_generation_failed", ex.ErrorCode);
        Assert.Contains("proxy rewrites", ex.Message);
    }

    [Fact]
    public void ValidateOrThrow_RejectsVercelJsonInvalidHeaderSourcePattern()
    {
        var ex = Assert.Throws<DeployAIException>(() =>
            GeneratedDeploymentFileValidator.ValidateOrThrow([
                ("client/vercel.json", """
                    {
                      "headers": [
                        {
                          "source": "/(.*\\.(js|css|woff|woff2|ttf|eot|svg|png|jpg|jpeg|gif|ico|webp))",
                          "headers": [{ "key": "Cache-Control", "value": "public, max-age=31536000, immutable" }]
                        }
                      ]
                    }
                    """)
            ]));

        Assert.Equal("setup_generation_failed", ex.ErrorCode);
        Assert.Contains("invalid headers source pattern", ex.Message);
    }

    [Fact]
    public void ValidateOrThrow_RejectsProgramCsAllowAllWithoutAllowedOrigins()
    {
        var ex = Assert.Throws<DeployAIException>(() =>
            GeneratedDeploymentFileValidator.ValidateOrThrow([
                ("src/Api/Program.cs", """
                    builder.Services.AddCors(options =>
                    {
                        options.AddPolicy("AllowAll", policy => policy.AllowAnyOrigin());
                    });
                    """)
            ]));

        Assert.Equal("setup_generation_failed", ex.ErrorCode);
        Assert.Contains("AllowAll", ex.Message);
    }
}
