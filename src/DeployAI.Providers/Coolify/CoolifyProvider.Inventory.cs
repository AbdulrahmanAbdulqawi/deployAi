using System.Net.Http.Json;
using System.Text.Json;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;

namespace DeployAI.Providers.Coolify;

/// <summary>
/// Read-only inventory of everything the instance runs. See <see cref="CoolifyInventory"/> for
/// why it exists; this file is only the join.
/// </summary>
public sealed partial class CoolifyProvider
{
    /// <summary>
    /// Lists projects, their environments, and every application and database, placed under the
    /// environment whose numeric id they carry. Four GETs — the same ones creation and database
    /// provisioning already make — never a per-application fetch, so the cost is flat in the
    /// number of apps.
    /// </summary>
    public async Task<CoolifyInventory> GetInventoryAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);

        var projects = await ListCoolifyProjectsAsync(session, cancellationToken);
        var applications = await ListInventoryApplicationsAsync(session, cancellationToken);
        var databases = await ListInventoryDatabasesAsync(session, cancellationToken);

        var placedApplications = new HashSet<string>(StringComparer.Ordinal);
        var placedDatabases = new HashSet<string>(StringComparer.Ordinal);
        var inventoryProjects = new List<CoolifyInventoryProject>(projects.Count);

        foreach (var project in projects)
        {
            // A listing that fails is recorded as such rather than as "no environments": a
            // project rendered empty because Coolify would not answer reads as a project with
            // nothing in it, which is the wrong conclusion to hand anyone.
            var environments = await TryListEnvironmentOptionsAsync(session, project.Uuid, cancellationToken);
            if (environments is null)
            {
                inventoryProjects.Add(new CoolifyInventoryProject(project.Uuid, project.Name ?? project.Uuid, [], EnvironmentsInconclusive: true));
                continue;
            }

            var inventoryEnvironments = new List<CoolifyInventoryEnvironment>(environments.Count);
            foreach (var environment in environments)
            {
                var apps = applications
                    .Where(app => app.EnvironmentId is not null && app.EnvironmentId == environment.Id)
                    .Select(app => app.Application)
                    .ToList();
                var dbs = databases
                    .Where(db => db.EnvironmentId is not null && db.EnvironmentId == environment.Id)
                    .Select(db => db.Database)
                    .ToList();

                foreach (var app in apps)
                {
                    placedApplications.Add(app.Uuid);
                }

                foreach (var db in dbs)
                {
                    placedDatabases.Add(db.Uuid);
                }

                inventoryEnvironments.Add(new CoolifyInventoryEnvironment(environment.Uuid, environment.Name, apps, dbs));
            }

            inventoryProjects.Add(new CoolifyInventoryProject(project.Uuid, project.Name ?? project.Uuid, inventoryEnvironments, EnvironmentsInconclusive: false));
        }

        return new CoolifyInventory(
            inventoryProjects,
            applications.Where(app => !placedApplications.Contains(app.Application.Uuid)).Select(app => app.Application).ToList(),
            databases.Where(db => !placedDatabases.Contains(db.Database.Uuid)).Select(db => db.Database).ToList());
    }

    private async Task<List<CoolifyEnvironmentOption>?> TryListEnvironmentOptionsAsync(
        CoolifyApiSupport.CoolifySession session,
        string projectUuid,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, $"projects/{projectUuid}/environments");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<List<CoolifyEnvironmentOption>>(cancellationToken) ?? [];
    }

    private async Task<List<(int? EnvironmentId, CoolifyInventoryApplication Application)>> ListInventoryApplicationsAsync(
        CoolifyApiSupport.CoolifySession session,
        CancellationToken cancellationToken)
    {
        var elements = await ListJsonArrayAsync(session, "applications", "applications", cancellationToken);
        var result = new List<(int?, CoolifyInventoryApplication)>(elements.Count);

        foreach (var element in elements)
        {
            var uuid = ReadString(element, "uuid");
            if (string.IsNullOrWhiteSpace(uuid))
            {
                continue;
            }

            // A compose app routes per service through docker_compose_domains and never has a
            // top-level fqdn; a plain app has fqdn as a comma-separated list. Both are read the
            // way the domain read-back already does, so the inventory and the deploy agree.
            var domains = new List<string>();
            if (ReadString(element, "fqdn") is { } fqdn)
            {
                domains.AddRange(fqdn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            foreach (var field in new[] { "docker_compose_domains", "parsedServiceDomains" })
            {
                if (element.TryGetProperty(field, out var composeDomains) &&
                    TryReadComposeDomains(composeDomains, out var serviceDomains))
                {
                    domains.AddRange(serviceDomains.Where(domain => !domains.Contains(domain, StringComparer.OrdinalIgnoreCase)));
                    break;
                }
            }

            result.Add((
                ReadInt(element, "environment_id"),
                new CoolifyInventoryApplication(
                    uuid,
                    ReadString(element, "name") ?? uuid,
                    ReadString(element, "build_pack"),
                    ReadString(element, "status"),
                    ReadString(element, "git_repository"),
                    ReadString(element, "git_branch"),
                    domains)));
        }

        return result;
    }

    private async Task<List<(int? EnvironmentId, CoolifyInventoryDatabase Database)>> ListInventoryDatabasesAsync(
        CoolifyApiSupport.CoolifySession session,
        CancellationToken cancellationToken)
    {
        var elements = await ListJsonArrayAsync(session, "databases", "databases", cancellationToken);
        var result = new List<(int?, CoolifyInventoryDatabase)>(elements.Count);

        foreach (var element in elements)
        {
            var uuid = ReadString(element, "uuid");
            if (string.IsNullOrWhiteSpace(uuid))
            {
                continue;
            }

            result.Add((
                ReadInt(element, "environment_id"),
                new CoolifyInventoryDatabase(
                    uuid,
                    ReadString(element, "name") ?? uuid,
                    // database_type is the engine (standalone-postgresql); older payloads only
                    // carried type. Same fallback the duplicate-prevention check uses.
                    ReadString(element, "database_type") ?? ReadString(element, "type"),
                    ReadString(element, "status"))));
        }

        return result;
    }

    private async Task<List<JsonElement>> ListJsonArrayAsync(
        CoolifyApiSupport.CoolifySession session,
        string path,
        string what,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, path);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(body) ?? $"Could not list Coolify {what} ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(element => element.Clone()).ToList()
            : [];
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
