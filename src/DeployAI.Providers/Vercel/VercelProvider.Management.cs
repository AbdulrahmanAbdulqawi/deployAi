using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Vercel;

public sealed partial class VercelProvider
{
    public async Task<ProviderProject> CreateProjectAsync(
        ProviderCredentials credentials,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        var projectName = VercelApiSupport.SanitizeProjectName(request.Name);
        var gitHubRepo = VercelApiSupport.NormalizeGitHubRepo(request.GitHubRepoFullName);

        var body = new Dictionary<string, object?>
        {
            ["name"] = projectName,
            ["gitRepository"] = new Dictionary<string, string>
            {
                ["type"] = "github",
                ["repo"] = gitHubRepo
            }
        };

        if (!string.IsNullOrWhiteSpace(request.Framework))
        {
            body["framework"] = request.Framework;
        }

        using var httpRequest = CreateRequest(HttpMethod.Post, "v11/projects", credentials.Token);
        httpRequest.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
        var project = await response.Content.ReadFromJsonAsync<VercelProjectDetail>(cancellationToken)
            ?? throw new InvalidOperationException("Vercel returned an empty project response.");

        if (!string.IsNullOrWhiteSpace(request.RootDirectory) ||
            !string.IsNullOrWhiteSpace(request.OutputDirectory) ||
            !string.IsNullOrWhiteSpace(request.BuildCommand) ||
            !string.IsNullOrWhiteSpace(request.Framework))
        {
            await ApplyProjectSettingsAsync(credentials, project.Id, request, cancellationToken);
        }

        return new ProviderProject(project.Id, project.Name, null);
    }

    private async Task SetRootDirectoryAsync(
        ProviderCredentials credentials,
        string projectId,
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        var body = VercelApiSupport.BuildSettingsFromEnvironment(
            new Dictionary<string, string> { ["rootDirectory"] = rootDirectory });
        await ApplyProjectSettingsAsync(credentials, projectId, body, cancellationToken);
    }

    internal static Dictionary<string, object?> BuildProjectSettingsPatch(string? rootDirectory, string? framework)
    {
        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(rootDirectory))
        {
            environment["rootDirectory"] = rootDirectory;
        }

        if (!string.IsNullOrWhiteSpace(framework))
        {
            environment["framework"] = framework;
        }

        return VercelApiSupport.BuildSettingsFromEnvironment(environment);
    }

    public async Task<IReadOnlyList<ProviderEnvVar>> ListEnvVarsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"v10/projects/{providerProjectId}/env", credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
        var envs = await response.Content.ReadFromJsonAsync<List<VercelEnvVar>>(cancellationToken) ?? [];

        return envs.Select(e => new ProviderEnvVar(
            e.Id ?? string.Empty,
            e.Key ?? string.Empty,
            ShouldHideValue(e.Type) ? null : e.Value,
            e.Type ?? "encrypted",
            e.Target ?? [],
            ShouldHideValue(e.Type))).ToList();
    }

    public async Task<ProviderEnvVar> UpsertEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        UpsertProviderEnvVarRequest request,
        CancellationToken cancellationToken)
    {
        var body = new[]
        {
            new
            {
                key = request.Key,
                value = request.Value,
                type = request.Type,
                target = request.Targets
            }
        };

        using var httpRequest = CreateRequest(HttpMethod.Post, $"v10/projects/{providerProjectId}/env?upsert=true", credentials.Token);
        httpRequest.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<List<VercelEnvVar>>(cancellationToken);
        var env = created?.FirstOrDefault()
            ?? throw new InvalidOperationException("Vercel returned an empty env var response.");

        return new ProviderEnvVar(
            env.Id ?? string.Empty,
            env.Key ?? request.Key,
            ShouldHideValue(env.Type) ? null : env.Value,
            env.Type ?? request.Type,
            env.Target ?? request.Targets.ToList(),
            ShouldHideValue(env.Type));
    }

    public async Task DeleteEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string envVarId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"v10/projects/{providerProjectId}/env/{envVarId}", credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteProjectAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"v9/projects/{providerProjectId}", credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
    }

    private static bool ShouldHideValue(string? type) =>
        string.Equals(type, "secret", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "sensitive", StringComparison.OrdinalIgnoreCase);

    private sealed class VercelProjectDetail
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class VercelEnvVar
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("target")]
        public List<string>? Target { get; set; }
    }
}
