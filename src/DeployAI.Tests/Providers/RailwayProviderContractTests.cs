using System.Net;
using System.Text;
using System.Text.Json;
using DeployAI.Core.Providers;
using DeployAI.Providers.Railway;
using DeployAI.Providers.Railway.GraphQL;
using Microsoft.Extensions.DependencyInjection;
using RichardSzalay.MockHttp;

namespace DeployAI.Tests.Providers;

public class RailwayProviderContractTests
{
    private static RailwayProvider CreateProvider(MockHttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory(handler));
        var serviceProvider = services.BuildServiceProvider();
        var graphQlFactory = new RailwayGraphQlClientFactory
        {
            TestHttpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>()
        };
        return new RailwayProvider(graphQlFactory);
    }

    private sealed class TestHttpClientFactory(MockHttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = handler.ToHttpClient();
            client.BaseAddress = new Uri(RailwayGraphQlClientFactory.GraphQlEndpoint);
            return client;
        }
    }

    private static StringContent GraphQlContent(string responseBody) =>
        new(RailwayGraphQlTestSupport.EnhanceResponse(responseBody), Encoding.UTF8, "application/json");

    private static HttpResponseMessage VolumeInstancesResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = GraphQlContent("""{"data":{"environment":{"volumeInstances":{"edges":[]}}}}""")
        };

    private static HttpResponseMessage VariablesResponse(string variablesJson = "{}") =>
        new(HttpStatusCode.OK)
        {
            Content = GraphQlContent("{\"data\":{\"variables\":" + JsonSerializer.Serialize(variablesJson) + "}}")
        };

    private static bool IsVolumeInstancesRequest(string body) =>
        body.Contains("EnvironmentVolumeInstances", StringComparison.Ordinal) ||
        body.Contains("volumeInstances", StringComparison.Ordinal);

    private static bool IsVariablesRequest(string body) =>
        body.Contains("GetVariables", StringComparison.Ordinal) ||
        (body.Contains("variables(", StringComparison.Ordinal) && body.Contains("projectId", StringComparison.Ordinal));

    private static bool IsProjectServicesRequest(string body) =>
        body.Contains("ProjectServices", StringComparison.Ordinal);

    private static bool IsServiceContextRequest(string body) =>
        body.Contains("ServiceContext", StringComparison.Ordinal) ||
        body.Contains("ServiceProject", StringComparison.Ordinal);

    private static HttpResponseMessage ServiceContextResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = GraphQlContent("""{"data":{"service":{"projectId":"proj_1","project":{"workspaceId":"ws_1"}}}}""")
        };

    private static HttpResponseMessage ServiceInstanceRegionResponse(string region = "us-west1") =>
        new(HttpStatusCode.OK)
        {
            Content = GraphQlContent(
                "{\"data\":{\"service\":{\"serviceInstances\":{\"edges\":[{\"node\":{\"environmentId\":\"env_1\",\"region\":" +
                JsonSerializer.Serialize(region) +
                "}}]}}}}")
        };

    private static HttpResponseMessage VolumeCreateResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = GraphQlContent("""{"data":{"volumeCreate":{"id":"vol_new","name":"postgres-data"}}}""")
        };

    private static HttpResponseMessage MutationSuccessResponse(string field) =>
        new(HttpStatusCode.OK)
        {
            Content = GraphQlContent($"{{\"data\":{{\"{field}\":true}}}}")
        };

    private static HttpResponseMessage RedeployServiceResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = GraphQlContent("""{"data":{"serviceInstanceDeployV2":"dep_1"}}""")
        };

    private static HttpResponseMessage PostgresProjectServicesResponse(string image, string serviceId = "svc_pg") =>
        new(HttpStatusCode.OK)
        {
            Content = GraphQlContent($$"""
                {
                  "data": {
                    "project": {
                      "services": {
                        "edges": [{
                          "node": {
                            "id": "{{serviceId}}",
                            "name": "Postgres",
                            "serviceInstances": {
                              "edges": [{
                                "node": {
                                  "environmentId": "env_1",
                                  "source": { "image": "{{image}}" }
                                }
                              }]
                            }
                          }
                        }]
                      }
                    }
                  }
                }
                """)
        };

    private static void MockGraphQl(MockHttpMessageHandler handler, string responseBody)
    {
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(HttpStatusCode.OK, "application/json", RailwayGraphQlTestSupport.EnhanceResponse(responseBody));
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ReturnsTrue_WhenMeQuerySucceeds()
    {
        var handler = new MockHttpMessageHandler();
        MockGraphQl(handler, """{"data":{"me":{"id":"user_1"}}}""");

        var provider = CreateProvider(handler);
        var result = await provider.ValidateCredentialsAsync(new ProviderCredentials("token"), CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task ListProjectsAsync_MapsServices()
    {
        var handler = new MockHttpMessageHandler();
        MockGraphQl(handler, """
        {
          "data": {
            "projects": {
              "edges": [{
                "node": {
                  "id": "proj_1",
                  "name": "My Project",
                  "environments": {
                    "edges": [{ "node": { "id": "env_1", "name": "production" } }]
                  },
                  "services": {
                    "edges": [{ "node": { "id": "svc_1", "name": "api" } }]
                  }
                }
              }]
            }
          }
        }
        """);

        var provider = CreateProvider(handler);
        var projects = await provider.ListProjectsAsync(new ProviderCredentials("token"), CancellationToken.None);

        Assert.Single(projects);
        Assert.Equal("svc_1|env_1", projects[0].Id);
        Assert.Equal("My Project / api", projects[0].Name);
    }

    [Fact]
    public async Task ListProjectsAsync_FallsBackToWorkspaceQuery_WhenRootProjectsUnauthorized()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("ListProjects", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"errors":[{"message":"Not Authorized"}]}""")
                    };
                }

                if (body.Contains("ListWorkspaces", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""
                            {
                              "data": {
                                "me": {
                                  "workspaces": [{ "id": "ws_1", "name": "Personal" }]
                                }
                              }
                            }
                            """)
                    };
                }

                if (body.Contains("WorkspaceProjects", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""
                        {
                          "data": {
                            "workspace": {
                              "projects": {
                                "edges": [{
                                  "node": {
                                    "id": "proj_1",
                                    "name": "OAuth Project",
                                    "environments": {
                                      "edges": [{ "node": { "id": "env_1", "name": "production" } }]
                                    },
                                    "services": {
                                      "edges": [{ "node": { "id": "svc_1", "name": "api" } }]
                                    }
                                  }
                                }]
                              }
                            }
                          }
                        }
                        """)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"externalWorkspaces":[]}}""")
                };
            });

        var provider = CreateProvider(handler);
        var projects = await provider.ListProjectsAsync(new ProviderCredentials("token"), CancellationToken.None);

        Assert.Single(projects);
        Assert.Equal("svc_1|env_1", projects[0].Id);
        Assert.Equal("OAuth Project / api", projects[0].Name);
    }

    [Fact]
    public async Task TriggerDeploymentAsync_ReturnsDeploymentId()
    {
        var handler = new MockHttpMessageHandler();
        var callCount = 0;
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(_ =>
            {
                callCount++;
                var body = _.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("serviceInstanceUpdate", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"serviceInstanceUpdate":true}}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"serviceInstanceDeployV2":"dep_1"}}""")
                };
            });

        var provider = CreateProvider(handler);
        var response = await provider.TriggerDeploymentAsync(
            new ProviderCredentials("token"),
            "svc_1|env_1",
            "main",
            new Dictionary<string, string> { ["rootDirectory"] = "src/api" },
            CancellationToken.None);

        Assert.Equal("dep_1", response.DeploymentId);
        Assert.Null(response.DeployUrl);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task TriggerDeploymentAsync_SendsOnlyRootDirectory_ForDotnetFramework()
    {
        string? updateBody = null;
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("serviceInstanceUpdate", StringComparison.Ordinal))
                {
                    updateBody = body;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"serviceInstanceUpdate":true}}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"serviceInstanceDeployV2":"dep_1"}}""")
                };
            });

        var provider = CreateProvider(handler);
        await provider.TriggerDeploymentAsync(
            new ProviderCredentials("token"),
            "svc_1|env_1",
            "main",
            new Dictionary<string, string>
            {
                ["rootDirectory"] = "iDaara.Server",
                ["framework"] = "dotnet",
                ["buildCommand"] = "dotnet build",
                ["startCommand"] = "dotnet run"
            },
            CancellationToken.None);

        Assert.NotNull(updateBody);
        Assert.Contains("iDaara.Server", updateBody, StringComparison.Ordinal);
        Assert.DoesNotContain("buildCommand", updateBody, StringComparison.Ordinal);
        Assert.DoesNotContain("startCommand", updateBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TriggerDeploymentAsync_SendsBuildCommand_ForDotnetMonorepoLayout()
    {
        string? updateBody = null;
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("serviceInstanceUpdate", StringComparison.Ordinal))
                {
                    updateBody = body;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"serviceInstanceUpdate":true}}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"serviceInstanceDeployV2":"dep_1"}}""")
                };
            });

        var provider = CreateProvider(handler);
        await provider.TriggerDeploymentAsync(
            new ProviderCredentials("token"),
            "svc_1|env_1",
            "main",
            new Dictionary<string, string>
            {
                ["rootDirectory"] = "src",
                ["serviceDirectory"] = "src/DeployAI.Api",
                ["framework"] = "dotnet",
                ["buildCommand"] = "dotnet publish DeployAI.Api/DeployAI.Api.csproj -c Release -o out",
                ["startCommand"] = "dotnet out/DeployAI.Api.dll"
            },
            CancellationToken.None);

        Assert.NotNull(updateBody);
        Assert.Contains("\"rootDirectory\":\"src\"", updateBody, StringComparison.Ordinal);
        Assert.Contains("DeployAI.Api/DeployAI.Api.csproj", updateBody, StringComparison.Ordinal);
        Assert.Contains("DeployAI.Api.dll", updateBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TriggerDeploymentAsync_PassesCommitSha_WhenProvided()
    {
        string? deployBody = null;
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("serviceInstanceDeployV2", StringComparison.Ordinal))
                {
                    deployBody = body;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"serviceInstanceDeployV2":"dep_1"}}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"serviceInstanceUpdate":true}}""")
                };
            });

        var provider = CreateProvider(handler);
        await provider.TriggerDeploymentAsync(
            new ProviderCredentials("token"),
            "svc_1|env_1",
            "master",
            new Dictionary<string, string>
            {
                ["commitSha"] = "44225d8bb776c28e10e4b75a2aa9ce7cc325938b"
            },
            CancellationToken.None);

        Assert.NotNull(deployBody);
        Assert.Contains("44225d8bb776c28e10e4b75a2aa9ce7cc325938b", deployBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TriggerDeploymentAsync_ConfiguresMonorepoDockerBuild()
    {
        string? updateBody = null;
        string? variableBody = null;
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("serviceInstanceUpdate", StringComparison.Ordinal))
                {
                    updateBody = body;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"serviceInstanceUpdate":true}}""")
                    };
                }

                if (body.Contains("variableUpsert", StringComparison.Ordinal))
                {
                    variableBody = body;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"variableUpsert":true}}""")
                    };
                }

                if (body.Contains("ServiceProject", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"service":{"projectId":"proj_1"}}}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"serviceInstanceDeployV2":"dep_1"}}""")
                };
            });

        var provider = CreateProvider(handler);
        await provider.TriggerDeploymentAsync(
            new ProviderCredentials("token"),
            "svc_1|env_1",
            "main",
            new Dictionary<string, string>
            {
                ["framework"] = "docker",
                ["dockerfilePath"] = "iDaara.Server/Dockerfile",
                ["rootDirectory"] = "."
            },
            CancellationToken.None);

        Assert.NotNull(updateBody);
        Assert.Contains("\"rootDirectory\":\".\"", updateBody, StringComparison.Ordinal);
        Assert.NotNull(variableBody);
        Assert.Contains("RAILWAY_DOCKERFILE_PATH", variableBody, StringComparison.Ordinal);
        Assert.Contains("iDaara.Server/Dockerfile", variableBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatusAsync_MapsSuccess()
    {
        var handler = new MockHttpMessageHandler();
        MockGraphQl(handler, """
        {
          "data": {
            "deployment": {
              "id": "dep_1",
              "status": "SUCCESS",
              "url": "https://api.up.railway.app"
            }
          }
        }
        """);

        var provider = CreateProvider(handler);
        var status = await provider.GetStatusAsync(new ProviderCredentials("token"), "dep_1", CancellationToken.None);
        Assert.Equal(DeploymentStatusKind.Success, status.Status);
        Assert.Equal("https://api.up.railway.app", status.DeployUrl);
    }

    [Fact]
    public async Task CreateProjectAsync_ReturnsCompositeServiceId()
    {
        var handler = new MockHttpMessageHandler();
        var callCount = 0;
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(_ =>
            {
                callCount++;
                var body = _.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("workspaces", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""
                            {
                              "data": {
                                "me": {
                                  "workspaces": [
                                    { "id": "ws_1", "name": "Personal" }
                                  ]
                                }
                              }
                            }
                            """)
                    };
                }

                if (body.Contains("projectCreate", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""
                            {
                              "data": {
                                "projectCreate": {
                                  "id": "proj_1",
                                  "environments": {
                                    "edges": [{ "node": { "id": "env_1", "name": "production" } }]
                                  }
                                }
                              }
                            }
                            """)
                    };
                }

                if (body.Contains("serviceCreate", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"serviceCreate":{"id":"svc_1","name":"my-api"}}}""")
                    };
                }

                if (body.Contains("ProjectEnvironments", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""
                            {
                              "data": {
                                "project": {
                                  "environments": {
                                    "edges": [{ "node": { "id": "env_1", "name": "production" } }]
                                  }
                                }
                              }
                            }
                            """)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"serviceInstanceUpdate":true}}""")
                };
            });

        var provider = CreateProvider(handler);
        var project = await provider.CreateProjectAsync(
            new ProviderCredentials("token"),
            new CreateProviderProjectRequest("my-api", "owner/my-app", null, "src/api"),
            CancellationToken.None);

        Assert.Equal("svc_1|env_1", project.Id);
        Assert.Equal("my-api", project.Name);
    }

    [Fact]
    public async Task ListEnvVarsAsync_MapsVariables()
    {
        var handler = new MockHttpMessageHandler();
        var callCount = 0;
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(_ =>
            {
                callCount++;
                var body = _.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("ServiceProject", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"service":{"projectId":"proj_1"}}}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"variables":{"API_URL":"https://example.com","API_KEY":"secret-value"}}}""")
                };
            });

        var provider = CreateProvider(handler);
        var envVars = await provider.ListEnvVarsAsync(new ProviderCredentials("token"), "svc_1|env_1", CancellationToken.None);

        Assert.Equal(2, envVars.Count);
        Assert.Contains(envVars, v => v.Key == "API_URL" && v.Value == "https://example.com");
        Assert.Contains(envVars, v => v.Key == "API_KEY" && v.ValueHidden);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task UpsertEnvVarAsync_UsesVariableNameAsId()
    {
        var handler = new MockHttpMessageHandler();
        var callCount = 0;
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(_ =>
            {
                callCount++;
                var body = _.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("ServiceProject", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"service":{"projectId":"proj_1"}}}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"variableUpsert":true}}""")
                };
            });

        var provider = CreateProvider(handler);
        var envVar = await provider.UpsertEnvVarAsync(
            new ProviderCredentials("token"),
            "svc_1|env_1",
            new UpsertProviderEnvVarRequest("API_URL", "https://example.com", "plain", []),
            CancellationToken.None);

        Assert.Equal("API_URL", envVar.Id);
        Assert.Equal("API_URL", envVar.Key);
        Assert.True(envVar.ValueHidden);
    }

    [Fact]
    public async Task StreamLogsAsync_UsesTopLevelBuildLogsAndYieldsMessages()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("DeploymentStatus", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"deployment":{"status":"SUCCESS"}}}""")
                    };
                }

                if (body.Contains("buildLogs(deploymentId:", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""
                            {
                              "data": {
                                "buildLogs": [
                                  { "message": "Building...", "timestamp": "2024-01-01T00:00:00Z" },
                                  { "message": "Build complete", "timestamp": "2024-01-01T00:01:00Z" }
                                ]
                              }
                            }
                            """)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""
                        {
                          "data": {
                            "deploymentLogs": [
                              { "message": "Server started", "timestamp": "2024-01-01T00:02:00Z" }
                            ]
                          }
                        }
                        """)
                };
            });

        var provider = CreateProvider(handler);
        var logs = new List<string>();
        await foreach (var line in provider.StreamLogsAsync(new ProviderCredentials("token"), "dep_1", CancellationToken.None))
        {
            logs.Add(line);
        }

        Assert.Equal(["Building...", "Build complete", "Server started"], logs);
    }

    [Fact]
    public async Task StreamLogsAsync_IgnoresMissingBuildDuringInitialization()
    {
        var handler = new MockHttpMessageHandler();
        var statusPolls = 0;
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("DeploymentStatus", StringComparison.Ordinal))
                {
                    statusPolls++;
                    var status = statusPolls < 2 ? "INITIALIZING" : "SUCCESS";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent($"{{\"data\":{{\"deployment\":{{\"status\":\"{status}\"}}}}}}")
                    };
                }

                if (body.Contains("buildLogs(deploymentId:", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"errors":[{"message":"Deployment does not have an associated build"}]}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"deploymentLogs":[]}}""")
                };
            });

        var provider = CreateProvider(handler);
        var logs = new List<string>();
        await foreach (var line in provider.StreamLogsAsync(new ProviderCredentials("token"), "dep_1", CancellationToken.None))
        {
            logs.Add(line);
        }

        Assert.Empty(logs);
        Assert.True(statusPolls >= 2);
    }

    [Fact]
    public async Task EnsurePostgresAsync_ReusesExistingService_WhenPostgresAlreadyInProject()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (IsServiceContextRequest(body))
                {
                    return ServiceContextResponse();
                }

                if (body.Contains("ServiceInstanceRegion", StringComparison.Ordinal))
                {
                    return ServiceInstanceRegionResponse();
                }

                if (IsProjectServicesRequest(body))
                {
                    return PostgresProjectServicesResponse("ghcr.io/railwayapp-templates/postgres-ssl:16");
                }

                if (body.Contains("VariableUpsert", StringComparison.Ordinal))
                {
                    return MutationSuccessResponse("variableUpsert");
                }

                if (body.Contains("VolumeCreate", StringComparison.Ordinal))
                {
                    return VolumeCreateResponse();
                }

                if (IsVolumeInstancesRequest(body))
                {
                    return VolumeInstancesResponse();
                }

                if (IsVariablesRequest(body))
                {
                    return VariablesResponse();
                }

                if (body.Contains("RedeployService", StringComparison.Ordinal))
                {
                    return RedeployServiceResponse();
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{}}""")
                };
            });

        var provider = CreateProvider(handler);
        var result = await provider.EnsurePostgresAsync(
            new ProviderCredentials("token"),
            "svc_app|env_1",
            null,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("svc_pg", result!.ServiceId);
        Assert.Equal("Postgres", result.ServiceName);
        Assert.Equal("proj_1", result.ProjectId);
    }

    [Fact]
    public async Task EnsurePostgresAsync_RepairsExistingBrokenService_WhenNamedPostgresExists()
    {
        var handler = new MockHttpMessageHandler();
        var updateBodies = new List<string>();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (IsServiceContextRequest(body))
                {
                    return ServiceContextResponse();
                }

                if (body.Contains("ServiceInstanceRegion", StringComparison.Ordinal))
                {
                    return ServiceInstanceRegionResponse();
                }

                if (body.Contains("serviceInstanceUpdate", StringComparison.Ordinal))
                {
                    updateBodies.Add(body);
                    return MutationSuccessResponse("serviceInstanceUpdate");
                }

                if (IsProjectServicesRequest(body))
                {
                    return PostgresProjectServicesResponse("ghcr.io/railway/postgres:16");
                }

                if (body.Contains("VariableUpsert", StringComparison.Ordinal))
                {
                    return MutationSuccessResponse("variableUpsert");
                }

                if (body.Contains("VolumeCreate", StringComparison.Ordinal))
                {
                    return VolumeCreateResponse();
                }

                if (IsVolumeInstancesRequest(body))
                {
                    return VolumeInstancesResponse();
                }

                if (IsVariablesRequest(body))
                {
                    return VariablesResponse();
                }

                if (body.Contains("RedeployService", StringComparison.Ordinal))
                {
                    return RedeployServiceResponse();
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{}}""")
                };
            });

        var provider = CreateProvider(handler);
        var result = await provider.EnsurePostgresAsync(
            new ProviderCredentials("token"),
            "svc_app|env_1",
            null,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("svc_pg", result!.ServiceId);
        Assert.Single(updateBodies);
        Assert.Contains("ghcr.io/railwayapp-templates/postgres-ssl:16", updateBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkDatabaseVariablesAsync_UpsertsReferences()
    {
        var handler = new MockHttpMessageHandler();
        var upsertBodies = new List<string>();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("variableUpsert", StringComparison.Ordinal))
                {
                    upsertBodies.Add(body);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"variableUpsert":true}}""")
                    };
                }

                if (body.Contains("ServiceProject", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"service":{"projectId":"proj_1"}}}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{}}""")
                };
            });

        var provider = CreateProvider(handler);
        await provider.LinkDatabaseVariablesAsync(
            new ProviderCredentials("token"),
            "svc_app|env_1",
            [
                new DatabaseVariableLink(
                    "ConnectionStrings__DefaultConnection",
                    "Host=${{Postgres.RAILWAY_PRIVATE_DOMAIN}};Port=5432;Database=${{Postgres.POSTGRES_DB}};Username=${{Postgres.POSTGRES_USER}};Password=${{Postgres.POSTGRES_PASSWORD}}"),
                new DatabaseVariableLink("ConnectionStrings__Redis", "${{Redis.REDIS_URL}}")
            ],
            CancellationToken.None);

        Assert.Equal(2, upsertBodies.Count);
        Assert.Contains(upsertBodies, b => b.Contains("ConnectionStrings__DefaultConnection", StringComparison.Ordinal));
        Assert.Contains(upsertBodies, b => b.Contains("${{Postgres.RAILWAY_PRIVATE_DOMAIN}}", StringComparison.Ordinal));
        Assert.Contains(upsertBodies, b => b.Contains("skipDeploys", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetServiceStatusAsync_ReturnsRunningStatus()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("ServiceStatus", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""
                            {
                              "data": {
                                "service": {
                                  "serviceInstances": {
                                    "edges": [{
                                      "node": {
                                        "environmentId": "env_1",
                                        "latestDeployment": {
                                          "status": "SUCCESS",
                                          "url": "https://example.up.railway.app",
                                          "createdAt": "2026-01-01T00:00:00Z"
                                        }
                                      }
                                    }]
                                  }
                                }
                              }
                            }
                            """)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{}}""")
                };
            });

        var provider = CreateProvider(handler);
        var status = await provider.GetServiceStatusAsync(
            new ProviderCredentials("token"),
            "svc_app|env_1",
            CancellationToken.None);

        Assert.Equal("running", status.Status);
        Assert.Equal("https://example.up.railway.app", status.DeployUrl);
    }

    [Fact]
    public async Task RedeployServiceAsync_UsesServiceInstanceDeploy()
    {
        var handler = new MockHttpMessageHandler();
        var bodies = new List<string>();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                bodies.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"serviceInstanceDeployV2":"dep_1"}}""")
                };
            });

        var provider = CreateProvider(handler);
        await provider.RedeployServiceAsync(
            new ProviderCredentials("token"),
            "svc_app|env_1",
            CancellationToken.None);

        Assert.Contains(bodies, b => b.Contains("serviceInstanceDeployV2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsurePostgresAsync_EnsuresVolumeMountPath_WhenPostgresExistsWithoutMount()
    {
        var handler = new MockHttpMessageHandler();
        var volumeMutations = new List<string>();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (IsVolumeInstancesRequest(body))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""
                            {
                              "data": {
                                "environment": {
                                  "volumeInstances": {
                                    "edges": [{
                                      "node": {
                                        "id": "vol_inst_1",
                                        "volumeId": "vol_1",
                                        "serviceId": "svc_pg",
                                        "mountPath": ""
                                      }
                                    }]
                                  }
                                }
                              }
                            }
                            """)
                    };
                }

                if (body.Contains("volumeInstanceUpdate", StringComparison.Ordinal) ||
                    body.Contains("volumeCreate", StringComparison.Ordinal) ||
                    body.Contains("serviceInstanceDeployV2", StringComparison.Ordinal))
                {
                    volumeMutations.Add(body);
                    if (body.Contains("volumeInstanceUpdate", StringComparison.Ordinal))
                    {
                        return MutationSuccessResponse("volumeInstanceUpdate");
                    }

                    if (body.Contains("volumeCreate", StringComparison.Ordinal))
                    {
                        return MutationSuccessResponse("volumeCreate");
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"serviceInstanceDeployV2":"dep_1"}}""")
                    };
                }

                if (IsServiceContextRequest(body))
                {
                    return ServiceContextResponse();
                }

                if (body.Contains("ServiceInstanceRegion", StringComparison.Ordinal))
                {
                    return ServiceInstanceRegionResponse();
                }

                if (IsProjectServicesRequest(body))
                {
                    return PostgresProjectServicesResponse("ghcr.io/railwayapp-templates/postgres-ssl:16");
                }

                if (body.Contains("VariableUpsert", StringComparison.Ordinal))
                {
                    return MutationSuccessResponse("variableUpsert");
                }

                if (IsVariablesRequest(body))
                {
                    return VariablesResponse();
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{}}""")
                };
            });

        var provider = CreateProvider(handler);
        var result = await provider.EnsurePostgresAsync(
            new ProviderCredentials("token"),
            "svc_app|env_1",
            "idaara",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(volumeMutations, b => b.Contains("volumeInstanceUpdate", StringComparison.Ordinal));
        Assert.Contains(volumeMutations, b => b.Contains("vol_1", StringComparison.Ordinal));
        Assert.Contains(volumeMutations, b => b.Contains("/var/lib/postgresql/data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsurePostgresAsync_RedeploysOnce_AfterVolumeAndVariableSetup()
    {
        var handler = new MockHttpMessageHandler();
        var deployCalls = 0;
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("serviceInstanceDeployV2", StringComparison.Ordinal))
                {
                    deployCalls++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"serviceInstanceDeployV2":"dep_1"}}""")
                    };
                }

                if (IsVolumeInstancesRequest(body))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""
                            {
                              "data": {
                                "environment": {
                                  "volumeInstances": {
                                    "edges": [{
                                      "node": {
                                        "id": "vol_inst_1",
                                        "volumeId": "vol_1",
                                        "serviceId": "svc_pg",
                                        "mountPath": ""
                                      }
                                    }]
                                  }
                                }
                              }
                            }
                            """)
                    };
                }

                if (body.Contains("volumeInstanceUpdate", StringComparison.Ordinal))
                {
                    return MutationSuccessResponse("volumeInstanceUpdate");
                }

                if (IsServiceContextRequest(body))
                {
                    return ServiceContextResponse();
                }

                if (body.Contains("ServiceInstanceRegion", StringComparison.Ordinal))
                {
                    return ServiceInstanceRegionResponse();
                }

                if (IsProjectServicesRequest(body))
                {
                    return PostgresProjectServicesResponse("ghcr.io/railwayapp-templates/postgres-ssl:16");
                }

                if (body.Contains("VariableUpsert", StringComparison.Ordinal))
                {
                    return MutationSuccessResponse("variableUpsert");
                }

                if (IsVariablesRequest(body))
                {
                    return VariablesResponse();
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{}}""")
                };
            });

        var provider = CreateProvider(handler);
        await provider.EnsurePostgresAsync(
            new ProviderCredentials("token"),
            "svc_app|env_1",
            "deployai",
            CancellationToken.None);

        Assert.Equal(1, deployCalls);
    }

    [Fact]
    public async Task DeleteServiceAsync_UsesServiceDeleteMutation()
    {
        var handler = new MockHttpMessageHandler();
        var bodies = new List<string>();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                bodies.Add(body);

                if (body.Contains("ServiceProject", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"service":{"projectId":"proj_1"}}}""")
                    };
                }

                if (body.Contains("EnvironmentVolumeInstances", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""
                            {
                              "data": {
                                "environment": {
                                  "volumeInstances": {
                                    "edges": [{
                                      "node": {
                                        "id": "vol_inst_1",
                                        "volumeId": "vol_1",
                                        "serviceId": "svc_app",
                                        "mountPath": "/var/lib/postgresql/data"
                                      }
                                    }]
                                  }
                                }
                              }
                            }
                            """)
                    };
                }

                if (body.Contains("volumeDelete", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = GraphQlContent("""{"data":{"volumeDelete":true}}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"serviceDelete":true}}""")
                };
            });

        var provider = CreateProvider(handler);
        await provider.DeleteServiceAsync(
            new ProviderCredentials("token"),
            "svc_app|env_1",
            CancellationToken.None);

        Assert.Contains(bodies, b => b.Contains("volumeDelete", StringComparison.Ordinal));
        Assert.Contains(bodies, b => b.Contains("serviceDelete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteProjectAsync_UsesProjectDeleteMutation()
    {
        var handler = new MockHttpMessageHandler();
        var bodies = new List<string>();
        handler.When(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
            .Respond(request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                bodies.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = GraphQlContent("""{"data":{"projectDelete":true}}""")
                };
            });

        var provider = CreateProvider(handler);
        await provider.DeleteProjectAsync(new ProviderCredentials("token"), "proj_1", CancellationToken.None);

        Assert.Contains(bodies, b => b.Contains("projectDelete", StringComparison.Ordinal));
    }
}
