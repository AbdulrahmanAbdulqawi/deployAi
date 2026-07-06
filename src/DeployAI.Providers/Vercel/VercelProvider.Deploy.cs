using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Vercel;

public sealed partial class VercelProvider
{
    private async Task<VercelProjectWithLink> GetProjectAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"v9/projects/{Uri.EscapeDataString(providerProjectId)}",
            credentials.Token);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await VercelApiSupport.EnsureSuccessAsync(response, cancellationToken);
        var project = await response.Content.ReadFromJsonAsync<VercelProjectWithLink>(cancellationToken)
            ?? throw new InvalidOperationException("Vercel returned an empty project response.");

        return project;
    }

    private static Dictionary<string, object?> BuildGitSource(
        string branch,
        VercelProjectLink? link,
        IReadOnlyDictionary<string, string> environment)
    {
        var gitSource = new Dictionary<string, object?>
        {
            ["type"] = "github",
            ["ref"] = branch
        };

        var repoId = link is null ? null : ParseRepoId(link.RepoId);
        if (repoId.HasValue)
        {
            gitSource["repoId"] = repoId.Value;
            return gitSource;
        }

        var githubRepo = environment.GetValueOrDefault("githubRepoFullName");
        if (string.IsNullOrWhiteSpace(githubRepo) && !string.IsNullOrWhiteSpace(link?.Repo))
        {
            githubRepo = link.Repo;
        }

        if (!string.IsNullOrWhiteSpace(githubRepo))
        {
            var parts = githubRepo.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                gitSource["org"] = parts[0];
                gitSource["repo"] = parts[1];
                return gitSource;
            }
        }

        throw new DeployAIException(
            "vercel_api_error",
            "This Vercel app is not linked to GitHub. Reconnect the repository in Vercel, then try publishing again.");
    }

    private static Dictionary<string, object?> BuildProjectSettings(
        VercelProjectWithLink project,
        IReadOnlyDictionary<string, string> environment)
    {
        var settings = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(project.Framework))
        {
            settings["framework"] = project.Framework;
        }

        if (!string.IsNullOrWhiteSpace(project.BuildCommand))
        {
            settings["buildCommand"] = project.BuildCommand;
        }

        if (!string.IsNullOrWhiteSpace(project.InstallCommand))
        {
            settings["installCommand"] = project.InstallCommand;
        }

        if (!string.IsNullOrWhiteSpace(project.OutputDirectory))
        {
            settings["outputDirectory"] = project.OutputDirectory;
        }

        if (environment.TryGetValue("rootDirectory", out var rootDirectory) && !string.IsNullOrWhiteSpace(rootDirectory))
        {
            settings["rootDirectory"] = rootDirectory.Trim().Trim('/');
        }
        else if (!string.IsNullOrWhiteSpace(project.RootDirectory))
        {
            settings["rootDirectory"] = project.RootDirectory;
        }

        foreach (var entry in VercelApiSupport.BuildSettingsFromEnvironment(environment))
        {
            settings[entry.Key] = entry.Value;
        }

        return settings;
    }

    private static long? ParseRepoId(JsonElement repoIdElement)
    {
        if (repoIdElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return repoIdElement.ValueKind switch
        {
            JsonValueKind.Number when repoIdElement.TryGetInt64(out var numericId) => numericId,
            JsonValueKind.String when long.TryParse(repoIdElement.GetString(), out var parsedId) => parsedId,
            _ => null
        };
    }

    private sealed class VercelProjectWithLink
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("framework")]
        public string? Framework { get; set; }

        [JsonPropertyName("buildCommand")]
        public string? BuildCommand { get; set; }

        [JsonPropertyName("installCommand")]
        public string? InstallCommand { get; set; }

        [JsonPropertyName("outputDirectory")]
        public string? OutputDirectory { get; set; }

        [JsonPropertyName("rootDirectory")]
        public string? RootDirectory { get; set; }

        [JsonPropertyName("link")]
        public VercelProjectLink? Link { get; set; }

        [JsonPropertyName("alias")]
        public List<string>? Aliases { get; set; }
    }

    private sealed class VercelProjectLink
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("repo")]
        public string? Repo { get; set; }

        [JsonPropertyName("repoId")]
        public JsonElement RepoId { get; set; }
    }
}
