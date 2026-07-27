using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Coolify;

public sealed partial class CoolifyProvider : IProviderApplicationConfigSync
{
    public async Task UpdateApplicationConfigAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        UpdateProviderApplicationConfigRequest request,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var buildPack = CoolifyApiSupport.ResolveBuildPack(
            request.CoolifyBuildPack,
            request.DockerfilePath,
            request.Framework,
            request.OutputDirectory,
            request.BuildCommand);

        var body = new Dictionary<string, object?>
        {
            ["build_pack"] = buildPack,
            ["ports_exposes"] = CoolifyApiSupport.ResolveExposedPort(buildPack),
            // Coolify caches the Traefik labels it generates at first deploy in custom_labels and
            // never regenerates them on redeploy unless this field is cleared - without this, a
            // build pack/port change here silently has no effect on the live proxy until someone
            // notices and clears it by hand (see the manual fix this method replaces). Like
            // custom_nginx_configuration, Coolify's validator requires this field to be base64
            // encoded - and rejects an empty string as "not base64" (its regex likely requires at
            // least one base64 character), so encode an empty JSON array instead of an empty string.
            ["custom_labels"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("[]"))
        };

        if (!string.IsNullOrWhiteSpace(request.RootDirectory))
        {
            body["base_directory"] = CoolifyApiSupport.NormalizeDirectoryPath(request.RootDirectory);
        }

        if (!string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            body["publish_directory"] = CoolifyApiSupport.NormalizeDirectoryPath(request.OutputDirectory);
        }

        if (!string.IsNullOrWhiteSpace(request.BuildCommand))
        {
            body["build_command"] = request.BuildCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.InstallCommand))
        {
            body["install_command"] = request.InstallCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.StartCommand))
        {
            body["start_command"] = request.StartCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.DockerfilePath))
        {
            body["dockerfile_location"] = request.DockerfilePath;
        }

        using var patchRequest = CreateRequest(HttpMethod.Patch, session, $"applications/{providerProjectId}");
        patchRequest.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(patchRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not update Coolify application config ({(int)response.StatusCode}).");
        }
    }

    public async Task<ProviderProject> CreateProjectAsync(
        ProviderCredentials credentials,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var projectUuid = await ResolveProjectUuidAsync(session, request, cancellationToken);
        var serverUuid = await ResolveServerUuidAsync(session, request, cancellationToken);
        var environment = await ResolveEnvironmentAsync(session, projectUuid, request, cancellationToken);
        var buildPack = CoolifyApiSupport.ResolveBuildPack(request);
        var gitRepository = CoolifyApiSupport.NormalizeGitHubRepoUrl(request.GitHubRepoFullName);
        var gitBranch = string.IsNullOrWhiteSpace(request.GitBranch) ? "main" : request.GitBranch.Trim();

        var body = new Dictionary<string, object?>
        {
            ["project_uuid"] = projectUuid,
            ["server_uuid"] = serverUuid,
            ["environment_name"] = environment.Name,
            ["environment_uuid"] = environment.Uuid,
            ["git_repository"] = gitRepository,
            ["git_branch"] = gitBranch,
            ["build_pack"] = buildPack,
            ["ports_exposes"] = CoolifyApiSupport.ResolveExposedPort(buildPack),
            ["name"] = request.Name.Trim(),
            ["instant_deploy"] = false,
            ["autogenerate_domain"] = true
        };

        if (!string.IsNullOrWhiteSpace(request.RootDirectory))
        {
            body["base_directory"] = CoolifyApiSupport.NormalizeDirectoryPath(request.RootDirectory);
        }

        if (!string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            body["publish_directory"] = CoolifyApiSupport.NormalizeDirectoryPath(request.OutputDirectory);
            body["is_static"] = true;
        }

        if (!string.IsNullOrWhiteSpace(request.BuildCommand))
        {
            body["build_command"] = request.BuildCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.InstallCommand))
        {
            body["install_command"] = request.InstallCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.StartCommand))
        {
            body["start_command"] = request.StartCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.DockerfilePath))
        {
            body["dockerfile_location"] = request.DockerfilePath;
        }

        string createPath;
        if (request.IsPrivateRepository)
        {
            var githubAppUuid = await ResolveGithubAppUuidAsync(session, request, cancellationToken);
            body["github_app_uuid"] = githubAppUuid;
            createPath = "applications/private-github-app";
        }
        else
        {
            createPath = "applications/public";
        }

        using var createRequest = CreateRequest(HttpMethod.Post, session, createPath);
        createRequest.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(createRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not create Coolify application ({(int)response.StatusCode}).");
        }

        var created = await response.Content.ReadFromJsonAsync<CoolifyCreateApplicationResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Coolify returned an empty application response.");

        if (string.IsNullOrWhiteSpace(created.Uuid))
        {
            throw new DeployAIException(
                "coolify_api_error",
                "Coolify did not return an application id.");
        }

        var application = await TryGetApplicationAsync(session, created.Uuid, cancellationToken);
        return new ProviderProject(
            created.Uuid,
            application?.Name ?? request.Name.Trim(),
            NormalizeUrl(application?.Fqdn),
            gitBranch);
    }

    public async Task<IReadOnlyList<ProviderEnvVar>> ListEnvVarsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        using var request = CreateRequest(HttpMethod.Get, session, $"applications/{providerProjectId}/envs");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not list Coolify environment variables ({(int)response.StatusCode}).");
        }

        var envs = await response.Content.ReadFromJsonAsync<List<CoolifyEnvironmentVariable>>(cancellationToken) ?? [];
        return envs
            .Where(env => !string.IsNullOrWhiteSpace(env.Key))
            .Select(env => new ProviderEnvVar(
                env.Uuid ?? env.Key!,
                env.Key!,
                env.IsShownOnce == true ? null : env.Value,
                "plain",
                [],
                env.IsShownOnce == true))
            .OrderBy(env => env.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ProviderEnvVar> UpsertEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        UpsertProviderEnvVarRequest request,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var existing = await ListEnvVarsAsync(credentials, providerProjectId, cancellationToken);
        var match = existing.FirstOrDefault(env =>
            string.Equals(env.Key, request.Key, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            using var patchRequest = CreateRequest(HttpMethod.Patch, session, $"applications/{providerProjectId}/envs");
            patchRequest.Content = JsonContent.Create(new
            {
                key = request.Key,
                value = request.Value
            });
            var patchResponse = await _httpClient.SendAsync(patchRequest, cancellationToken);
            var patchBody = await patchResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!patchResponse.IsSuccessStatusCode)
            {
                throw new DeployAIException(
                    "coolify_api_error",
                    CoolifyApiSupport.ParseErrorMessage(patchBody)
                        ?? $"Could not update Coolify environment variable ({(int)patchResponse.StatusCode}).");
            }

            var updated = await patchResponse.Content.ReadFromJsonAsync<CoolifyEnvironmentVariable>(cancellationToken);
            return new ProviderEnvVar(
                updated?.Uuid ?? match.Id,
                request.Key,
                null,
                request.Type,
                request.Targets.ToList(),
                true);
        }

        using var createRequest = CreateRequest(HttpMethod.Post, session, $"applications/{providerProjectId}/envs");
        createRequest.Content = JsonContent.Create(new
        {
            key = request.Key,
            value = request.Value
        });
        var createResponse = await _httpClient.SendAsync(createRequest, cancellationToken);
        var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(createBody)
                    ?? $"Could not create Coolify environment variable ({(int)createResponse.StatusCode}).");
        }

        var created = await createResponse.Content.ReadFromJsonAsync<CoolifyEnvironmentVariable>(cancellationToken)
            ?? throw new InvalidOperationException("Coolify returned an empty env var response.");

        return new ProviderEnvVar(
            created.Uuid ?? request.Key,
            request.Key,
            null,
            request.Type,
            request.Targets.ToList(),
            created.IsShownOnce == true);
    }

    public async Task DeleteEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string envVarId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        using var request = CreateRequest(
            HttpMethod.Delete,
            session,
            $"applications/{providerProjectId}/envs/{envVarId}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not delete Coolify environment variable ({(int)response.StatusCode}).");
        }
    }

    public async Task DeleteProjectAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        using var request = CreateRequest(HttpMethod.Delete, session, $"applications/{providerProjectId}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not delete Coolify application ({(int)response.StatusCode}).");
        }
    }

    /// <summary>Lists the projects, servers, and GitHub Apps visible on a Coolify connection, for the setup UI.</summary>
    public async Task<CoolifyInfrastructureSnapshot> ListInfrastructureAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var projects = await ListCoolifyProjectsAsync(session, cancellationToken);
        var servers = await ListCoolifyServersAsync(session, cancellationToken);
        var githubApps = await ListCoolifyGithubAppsAsync(session, cancellationToken);
        return new CoolifyInfrastructureSnapshot(
            projects.Select(MapInfrastructureResource).ToList(),
            servers.Select(MapInfrastructureResource).ToList(),
            githubApps.Select(MapInfrastructureResource).ToList());
    }

    /// <summary>Lists the environments (e.g. production, staging) within a Coolify project.</summary>
    public async Task<IReadOnlyList<CoolifyInfrastructureResource>> ListProjectEnvironmentsAsync(
        ProviderCredentials credentials,
        string projectUuid,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var environments = await ListCoolifyEnvironmentsAsync(session, projectUuid, cancellationToken);
        return environments
            .Select(env => new CoolifyInfrastructureResource(env.Uuid, env.Name))
            .ToList();
    }

    /// <summary>Resolves which Coolify project to create the application in: an explicit uuid if given, else the first existing project, else a newly created one.</summary>
    private async Task<string> ResolveProjectUuidAsync(
        CoolifyApiSupport.CoolifySession session,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.CoolifyProjectUuid))
        {
            return request.CoolifyProjectUuid;
        }

        var projects = await ListCoolifyProjectsAsync(session, cancellationToken);
        if (projects.Count > 0)
        {
            return projects[0].Uuid;
        }

        using var createRequest = CreateRequest(HttpMethod.Post, session, "projects");
        createRequest.Content = JsonContent.Create(new { name = request.Name.Trim() });
        var response = await _httpClient.SendAsync(createRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not create Coolify project ({(int)response.StatusCode}).");
        }

        var created = await response.Content.ReadFromJsonAsync<CoolifyUuidResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(created?.Uuid))
        {
            throw new DeployAIException(
                "coolify_api_error",
                "Coolify did not return a project id.");
        }

        return created.Uuid;
    }

    /// <summary>Resolves which Coolify server to deploy to: an explicit uuid if given, else the first configured server. Throws if none exist.</summary>
    private async Task<string> ResolveServerUuidAsync(
        CoolifyApiSupport.CoolifySession session,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.CoolifyServerUuid))
        {
            return request.CoolifyServerUuid;
        }

        var servers = await ListCoolifyServersAsync(session, cancellationToken);
        if (servers.Count == 0)
        {
            throw new DeployAIException(
                "coolify_no_server",
                "No servers are configured in your Coolify instance. Add a server in Coolify first.");
        }

        return servers[0].Uuid;
    }

    /// <summary>Resolves which environment to deploy to: an explicit name/uuid if given and found, else "production" if it exists, else the first environment.</summary>
    private async Task<CoolifyEnvironmentOption> ResolveEnvironmentAsync(
        CoolifyApiSupport.CoolifySession session,
        string projectUuid,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        var environments = await ListCoolifyEnvironmentsAsync(session, projectUuid, cancellationToken);
        if (environments.Count == 0)
        {
            throw new DeployAIException(
                "coolify_no_environment",
                "No environments are configured for the selected Coolify project.");
        }

        if (!string.IsNullOrWhiteSpace(request.CoolifyEnvironmentName))
        {
            var match = environments.FirstOrDefault(env =>
                string.Equals(env.Name, request.CoolifyEnvironmentName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(env.Uuid, request.CoolifyEnvironmentName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return environments.FirstOrDefault(env =>
                   string.Equals(env.Name, "production", StringComparison.OrdinalIgnoreCase))
               ?? environments[0];
    }

    /// <summary>Resolves the Coolify GitHub App for a private repo: an explicit uuid if given, else the sole configured app. Throws if none or more than one exist and none was chosen.</summary>
    private async Task<string> ResolveGithubAppUuidAsync(
        CoolifyApiSupport.CoolifySession session,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.CoolifyGithubAppUuid))
        {
            return request.CoolifyGithubAppUuid;
        }

        var githubApps = await ListCoolifyGithubAppsAsync(session, cancellationToken);
        if (githubApps.Count == 0)
        {
            throw new DeployAIException(
                "coolify_no_github_app",
                "Private repositories require a GitHub App configured in Coolify. Set one up in Coolify → Sources → GitHub Apps.");
        }

        if (githubApps.Count > 1)
        {
            throw new DeployAIException(
                "coolify_github_app_required",
                "Choose which Coolify GitHub App to use for this private repository.");
        }

        return githubApps[0].Uuid;
    }

    private async Task<CoolifyApplication?> TryGetApplicationAsync(
        CoolifyApiSupport.CoolifySession session,
        string applicationUuid,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, $"applications/{applicationUuid}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<CoolifyApplication>(cancellationToken);
    }

    private async Task<IReadOnlyList<CoolifyNamedResource>> ListCoolifyProjectsAsync(
        CoolifyApiSupport.CoolifySession session,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, "projects");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "Could not list Coolify projects.");
        var projects = await response.Content.ReadFromJsonAsync<List<CoolifyNamedResource>>(cancellationToken) ?? [];
        return projects.Where(project => !string.IsNullOrWhiteSpace(project.Uuid)).ToList();
    }

    private async Task<IReadOnlyList<CoolifyNamedResource>> ListCoolifyServersAsync(
        CoolifyApiSupport.CoolifySession session,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, "servers");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "Could not list Coolify servers.");
        var servers = await response.Content.ReadFromJsonAsync<List<CoolifyNamedResource>>(cancellationToken) ?? [];
        return servers.Where(server => !string.IsNullOrWhiteSpace(server.Uuid)).ToList();
    }

    private async Task<IReadOnlyList<CoolifyNamedResource>> ListCoolifyGithubAppsAsync(
        CoolifyApiSupport.CoolifySession session,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, "github-apps");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "Could not list Coolify GitHub apps.");
        var githubApps = await response.Content.ReadFromJsonAsync<List<CoolifyNamedResource>>(cancellationToken) ?? [];
        return githubApps.Where(app => !string.IsNullOrWhiteSpace(app.Uuid)).ToList();
    }

    private async Task<IReadOnlyList<CoolifyEnvironmentOption>> ListCoolifyEnvironmentsAsync(
        CoolifyApiSupport.CoolifySession session,
        string projectUuid,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, $"projects/{projectUuid}/environments");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "Could not list Coolify environments.");
        var environments = await response.Content.ReadFromJsonAsync<List<CoolifyEnvironmentOption>>(cancellationToken) ?? [];
        return environments
            .Where(env => !string.IsNullOrWhiteSpace(env.Uuid) && !string.IsNullOrWhiteSpace(env.Name))
            .ToList();
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        string fallbackMessage)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new DeployAIException(
            "coolify_api_error",
            CoolifyApiSupport.ParseErrorMessage(responseBody) ?? fallbackMessage);
    }

    private static CoolifyInfrastructureResource MapInfrastructureResource(CoolifyNamedResource resource) =>
        new(
            resource.Uuid,
            string.IsNullOrWhiteSpace(resource.Name) ? resource.Uuid : resource.Name);

    private sealed class CoolifyCreateApplicationResponse
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }
    }

    private sealed class CoolifyUuidResponse
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }
    }

    private sealed class CoolifyEnvironmentVariable
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("is_shown_once")]
        public bool? IsShownOnce { get; set; }
    }

    internal sealed class CoolifyNamedResource
    {
        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class CoolifyEnvironmentOption
    {
        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
